using Azure.AI.OpenAI;
using Azure.Communication;
using Azure.Communication.CallAutomation;
using Azure.Messaging;
using NAudio.Wave;
using OpenAI.RealtimeConversation;
using System.ClientModel;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

#pragma warning disable OPENAI002

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var acsConnectionString = builder.Configuration["ConnectionStrings:ACS"];
var azureOpenAiApiKey = builder.Configuration["ConnectionStrings:AzureOpenAI"];
var azureOpenAiEndpoint = builder.Configuration["ConnectionStrings:AzureOpenAIEndPoint"];
var cognitiveServicesEndpoint = builder.Configuration["ConnectionStrings:CognitiveServicesEndPoint"];
var callbackUriHost = builder.Configuration["ConnectionStrings:UriHost"];
var wssUri = callbackUriHost.Replace("https", "wss");

var callAutomationClient = new CallAutomationClient(acsConnectionString);
var app = builder.Build();

string audioFilesDirectory = Path.Combine(app.Environment.ContentRootPath, "AudioFiles");
Directory.CreateDirectory(audioFilesDirectory);

var chatSession = await InitializeChatSessionAsync();

var sampleRate = 16000;
var channels = 1;
var recordingId = string.Empty;
var readyToFinish = false;

app.MapPost("/outboundCall", async (ILogger<Program> logger) =>
{
    PhoneNumberIdentifier target = new PhoneNumberIdentifier(
        builder.Configuration["ConnectionStrings:TargetPhoneNumber"]);
    PhoneNumberIdentifier caller = new PhoneNumberIdentifier(
        builder.Configuration["ConnectionStrings:CallerPhoneNumber"]);

    var callbackUri = new Uri(new Uri(callbackUriHost), "/api/callbacks");
    
    CallInvite callInvite = new CallInvite(target, caller);

    var mediaStreamingOptions = new MediaStreamingOptions(
        new Uri(new Uri(wssUri), "/api/receiveAudio"),
        MediaStreamingContent.Audio,
        MediaStreamingAudioChannel.Mixed,
        MediaStreamingTransport.Websocket
    );

    var createCallOptions = new CreateCallOptions(callInvite, callbackUri)
    {
        CallIntelligenceOptions = new CallIntelligenceOptions() { CognitiveServicesEndpoint = new Uri(cognitiveServicesEndpoint) },
        MediaStreamingOptions = mediaStreamingOptions
    };

    CreateCallResult createCallResult = await callAutomationClient.CreateCallAsync(createCallOptions);

    logger.LogInformation($"Created call with connection id: {createCallResult.CallConnectionProperties.CallConnectionId}");

    // Inside the main setup
    var callConnectionId = createCallResult.CallConnectionProperties.CallConnectionId;
    await ReceiveAndProcessChatUpdatesAsync(chatSession, callConnectionId, logger);
});

app.MapPost("/api/callbacks", async (CloudEvent[] cloudEvents, ILogger<Program> logger) =>
{
    foreach (var cloudEvent in cloudEvents)
    {
        CallAutomationEventBase parsedEvent = CallAutomationEventParser.Parse(cloudEvent);
        logger.LogInformation(
                    "Received call event: {type}, callConnectionID: {connId}, serverCallId: {serverId}",
                    parsedEvent.GetType(),
                    parsedEvent.CallConnectionId,
                    parsedEvent.ServerCallId);

        var callConnection = callAutomationClient.GetCallConnection(parsedEvent.CallConnectionId);
        var callMedia = callConnection.GetCallMedia();
        var callRecording = callAutomationClient.GetCallRecording();

        if (parsedEvent is CallConnected callConnected)
        {
            logger.LogInformation("Call connected...");

            await callMedia.StartMediaStreamingAsync();

            StartRecordingOptions recordingOptions = new StartRecordingOptions(new ServerCallLocator(callConnected.ServerCallId))
            {
                RecordingContent = RecordingContent.Audio,
                RecordingChannel = RecordingChannel.Mixed,
                RecordingFormat = RecordingFormat.Wav,
                RecordingStorage = RecordingStorage.CreateAzureBlobContainerRecordingStorage(
                    new Uri(builder.Configuration["ConnectionStrings:RecordingsBlobStorageContainer"]))
            };
            var recordingResult = await callRecording.StartAsync(recordingOptions);
            recordingId = recordingResult.Value.RecordingId;
            logger.LogInformation($"Recording started with recordingId: {recordingId}");
        }
        else if (parsedEvent is CallDisconnected callDisconnected)
        {
            logger.LogInformation("Call disconnected...");

            // Stop the recording when the call is disconnected
            if (!string.IsNullOrEmpty(parsedEvent.CallConnectionId)
                && !string.IsNullOrEmpty(parsedEvent.ServerCallId)
                && !string.IsNullOrEmpty(recordingId))
            {
                await callRecording.StopAsync(recordingId);
                logger.LogInformation("Stopped recording");
            }
        }
        else if (parsedEvent is PlayCompleted playCompleted)
        {
            if (readyToFinish)
            {
                if (!string.IsNullOrEmpty(parsedEvent.CallConnectionId)
                    && !string.IsNullOrEmpty(parsedEvent.ServerCallId))
                {
                    if (!string.IsNullOrEmpty(recordingId))
                    {
                        await callRecording.StopAsync(recordingId);
                        recordingId = string.Empty;
                    }
                    await callAutomationClient.GetCallConnection(parsedEvent.CallConnectionId).HangUpAsync(true);
                    readyToFinish = false;
                }
            }
        }
        else if (parsedEvent is PlayFailed playFailed)
        {
            var resultInfo = playFailed.ResultInformation;
            logger.LogError("Play failed. Code: {code}, SubCode: {subCode}, Message: {message}",
                resultInfo?.Code, resultInfo?.SubCode, resultInfo?.Message);
        }
    }
    return Results.Ok();
}).Produces(StatusCodes.Status200OK);

app.Map("/api/receiveAudio", async (HttpContext context, ILogger<Program> logger) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
        return;
    }

    using WebSocket webSocket = await context.WebSockets.AcceptWebSocketAsync();
    await ReceiveAudioDataAsync(webSocket, logger);
});

app.MapGet("/audio/{filename}", async (string filename, HttpContext context) =>
{
    var filePath = Path.Combine(audioFilesDirectory, filename);

    if (!System.IO.File.Exists(filePath))
    {
        context.Response.StatusCode = (int)HttpStatusCode.NotFound;
        return;
    }

    context.Response.ContentType = "audio/wav";
    await context.Response.SendFileAsync(filePath).ContinueWith((Task t) => {
        File.Delete(filePath);
    });
});

async Task ReceiveAudioDataAsync(WebSocket webSocket, ILogger<Program> logger)
{
    byte[] buffer = new byte[2048];

    while (webSocket.State == WebSocketState.Open)
    {
        var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

        if (result.MessageType == WebSocketMessageType.Close)
        {
            await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by server", CancellationToken.None);
        }
        else
        {
            string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
            ProcessAudioMessage(message, logger);
        }
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseWebSockets();
app.Run();

async Task ReceiveAndProcessChatUpdatesAsync(
    RealtimeConversationSession chatSession, string callConnectionId, ILogger<Program> logger)
{
    var callConnection = callAutomationClient.GetCallConnection(callConnectionId);
    var callMedia = callConnection.GetCallMedia();
    using var accumulatedAudioStream = new MemoryStream();

    await foreach (var update in chatSession.ReceiveUpdatesAsync())
    {
        switch (update)
        {
            case ConversationAudioDeltaUpdate audioDeltaUpdate:
                accumulatedAudioStream.Write(audioDeltaUpdate.Delta?.ToArray() ?? Array.Empty<byte>());
                break;
                
            case ConversationItemFinishedUpdate itemFinishedUpdate:
                if (itemFinishedUpdate.MessageContentParts is not null 
                    && itemFinishedUpdate.MessageContentParts.Any())
                    logger.LogInformation($"ChatGPT: {itemFinishedUpdate.MessageContentParts[0].AudioTranscriptValue}");

                if (itemFinishedUpdate.FunctionCallId is not null)
                {
                    if (itemFinishedUpdate.FunctionName == "EndCall")
                    {
                        readyToFinish = true;
                        logger.LogInformation("About to end call.");
                    }
                }

                break;

            case ConversationResponseFinishedUpdate:
                logger.LogInformation(update.GetType().Name);
                if (accumulatedAudioStream.Length == 0)
                    break;

                accumulatedAudioStream.Seek(0, SeekOrigin.Begin);

                var uniqueFileName = $"{Guid.NewGuid()}.wav";
                var filePath = Path.Combine(audioFilesDirectory, uniqueFileName);

                //PlayG711AlawAudioFromMemory(accumulatedAudioStream.ToArray());
                SaveG711ALawTo16kHzPcmWav(accumulatedAudioStream.ToArray(), filePath);

                var audioUrl = new Uri(new Uri(callbackUriHost), $"/audio/{uniqueFileName}");

                var playSource = new FileSource(audioUrl);
                var playOptions = new PlayToAllOptions(playSource)
                {
                    Loop = false,
                    InterruptCallMediaOperation = true
                };

                var res = await callMedia.PlayToAllAsync(playOptions);

                accumulatedAudioStream.SetLength(0);

                if (readyToFinish)
                    return;

                break;

            case ConversationErrorUpdate errorUpdate:
                logger.LogError(errorUpdate.ErrorMessage);
                break;

            default:
                //logger.LogInformation(update.GetType().Name);
                break;
        }
    }
}

void PlayAudioFromMemory(byte[] audioData)
{
    using var ms = new MemoryStream(audioData);
    var waveFormat = new NAudio.Wave.WaveFormat(16000, 16, 1); // Adjust format if necessary
    using var waveProvider = new NAudio.Wave.RawSourceWaveStream(ms, waveFormat);
    using var waveOut = new NAudio.Wave.WaveOutEvent();

    waveOut.Init(waveProvider);
    waveOut.Play();

    while (waveOut.PlaybackState == NAudio.Wave.PlaybackState.Playing)
    {
        Thread.Sleep(100);
    }
}

void ProcessAudioMessage(string message, ILogger<Program> logger)
{
    try
    {
        var eventData = JsonSerializer.Deserialize<JsonElement>(message);
        string kind = eventData.GetProperty("kind").GetString();

        if (kind == "AudioMetadata")
        {
            var metadata = eventData.GetProperty("audioMetadata");
            sampleRate = metadata.GetProperty("sampleRate").GetInt32();
            channels = metadata.GetProperty("channels").GetInt32();
            int length = metadata.GetProperty("length").GetInt32();

            logger.LogInformation("Received Audio Metadata: SampleRate: {sampleRate}, Channels: {channels}, Length: {length}",
                sampleRate, channels, length);
        }
        else if (kind == "AudioData")
        {
            var audioData = eventData.GetProperty("audioData");
            byte[] audioBytes = Convert.FromBase64String(audioData.GetProperty("data").GetString());
            string timestamp = audioData.GetProperty("timestamp").GetString();
            bool isSilent = audioData.GetProperty("silent").GetBoolean();

            if (isSilent)
                logger.LogTrace("Silence");

            SendAudioFrameToChatGptAsync(audioBytes);
        }
    }
    catch (JsonException ex)
    {
        logger.LogError("Failed to deserialize audio message: {error}", ex.Message);
    }
}

async Task<RealtimeConversationSession> InitializeChatSessionAsync()
{
    RealtimeConversationClient client = GetConfiguredChatClient();
    var chatGptSession = await client.StartConversationSessionAsync();

    ConversationSessionOptions sessionOptions = new()
    {
        Instructions = "You are a personal assistant giving a call to dentist office. " +
        "Your goal is to agree for an appointement with the dentist for Dimitro Vakulenko. " +
        "You are his personal AI assistant. " +
        "Dimitro is available for an appointment on Tusedays afternoon and Fridays between 2pm and 4pm. " +
        "He is not available at other times." +
        "When you are giving a call act normally, keep short sentences, do not output too much info at once. " +
        "For example start with hello and explaining what you are (an AI assistant of Dmytro Vakulenko) " +
        "and that you would like to make an appointment, later depending on the answer from " +
        "the dentist office discuss the time for the appointemnt. " +
        "Once a time is agreed upon or there is no suitable time, please call the 'EndCall' tool to terminate the conversation. " +
        "Don't forget thank you and good bye before ending the call.",
        InputAudioFormat = ConversationAudioFormat.Pcm16,
        OutputAudioFormat = ConversationAudioFormat.G711Alaw,
        Voice = ConversationVoice.Alloy,
        Temperature = 0.7f,
        MaxResponseOutputTokens = 1200,
        TurnDetectionOptions = 
            ConversationTurnDetectionOptions.CreateServerVoiceActivityTurnDetectionOptions(
                0.5f, 
                TimeSpan.FromMilliseconds(200),
                TimeSpan.FromMilliseconds(300))
    };

    sessionOptions.Tools.Add(new ConversationFunctionTool()
    {
        Name = "EndCall",
        Description = "This tool ends the call when an appointmnt is agreed or no appointment date is availablle."
    });

    await chatGptSession.ConfigureSessionAsync(sessionOptions);
    return chatGptSession;
}

RealtimeConversationClient GetConfiguredChatClient()
{
    AzureOpenAIClient azureOpenAiClient = new(new Uri(azureOpenAiEndpoint), new ApiKeyCredential(azureOpenAiApiKey));
    return azureOpenAiClient.GetRealtimeConversationClient("gpt-4o-realtime-preview");
}

async Task SendAudioFrameToChatGptAsync(byte[] audioBytes)
{
    using var audioStream = new MemoryStream(audioBytes);
    await chatSession.SendAudioAsync(audioStream);
}

void SaveG711ALawTo16kHzPcmWav(byte[] g711AlawData, string filePath)
{
    var alawFormat = WaveFormat.CreateALawFormat(8000, 1); // Original G.711 A-law format: 8 kHz, mono
    var targetFormat = new WaveFormat(16000, 16, 1);       // Target format: 16 kHz, 16-bit, mono

    using (var ms = new MemoryStream(g711AlawData))
    using (var alawStream = new RawSourceWaveStream(ms, alawFormat))
    using (var pcmStream = WaveFormatConversionStream.CreatePcmStream(alawStream))
    using (var resampler = new MediaFoundationResampler(pcmStream, targetFormat))
    {
        resampler.ResamplerQuality = 60;
        WaveFileWriter.CreateWaveFile(filePath, resampler);
    }
}

void PlayG711AlawAudioFromMemory(byte[] g711AlawData)
{
    var alawFormat = WaveFormat.CreateALawFormat(8000, 1); // 8 kHz, Mono (G.711 A-law format)

    using var ms = new MemoryStream(g711AlawData);
    using var alawStream = new RawSourceWaveStream(ms, alawFormat);

    using var pcmStream = WaveFormatConversionStream.CreatePcmStream(alawStream);
    using var waveOut = new WaveOutEvent();

    waveOut.Init(pcmStream);
    waveOut.Play();

    while (waveOut.PlaybackState == PlaybackState.Playing)
    {
        Thread.Sleep(100);
    }
}