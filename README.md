# CallAssistantPOC: Call Automation with Azure OpenAI GPT-4o Realtime & ACS

This POC leverages Azure OpenAI's GPT-4o Realtime API and Azure Communication Services (ACS) to create an interactive, low-latency automated calling system. 

Designed for scenarios like appointment scheduling, this system can handle real-time conversations by directly connecting callers with the GPT-4o model, bypassing the need for separate speech-to-text and text-to-speech services.

## Table of Contents
- [Getting Started](#getting-started)
- [Configuration](#configuration)
- [License](#license)

## Getting Started

1. **Azure Resources Setup**:
   - **Azure Communication Services (ACS)**:
     1. Create an ACS resource in the Azure portal.
     2. Go to the ACS resource, navigate to "Phone numbers," and obtain a phone number.
     3. Save the ACS endpoint and access key for use in configuration.

   - **Azure OpenAI**:
     1. Set up an Azure OpenAI instance with the `gpt-4o-realtime-preview` model.
     2. Obtain the endpoint and API key to configure GPT-4o Realtime access.
     
   - **Azure Blob Storage**:
     1. Create a blob container for storing call recordings.
     2. Assign Storage Blob Data Contributor Role to ACS Managed Identity on the blob storage resource.

2. **Define System Instructions for Chatbot**:
   In the `InitializeChatSessionAsync` method, define the system message/instructions for the assistant (ChatGPT):
   - For example:
     ```csharp
     ConversationSessionOptions sessionOptions = new()
     {
         Instructions = "You are a personal assistant giving a call to the dentist office. " +
         "Your goal is to agree on an appointment with the dentist for Dimitro Vakulenko. " +
         "You are his personal AI assistant. Dimitro is available for an appointment on " +
         "Tuesdays mornings and Fridays between 2pm and 4pm. He is not available at other times. " +
         "When giving a call, act normally, keep sentences short, and do not output too much info at once. " +
         "Once a time is agreed upon or there is no suitable time, call the 'EndCall' tool to terminate the conversation. " +
         "Don't forget to say thank you and goodbye before ending the call.",
         ...
     };
     ```
   This allows the assistant to follow a structured conversation flow, ensuring that calls end automatically when the appointment is confirmed or when no time is available.

## Configuration

Edit configuration file named `appsettings.json` in the project’s root directory. 
Replace placeholders with your actual Azure resource details:
(For local tests one can use microsoft dev-tunnels)

```json
{
  "ConnectionStrings": {
    "CognitiveServicesEndPoint": "https://<your-acs-endpoint>.communication.azure.com/",
    "ACS": "endpoint=https://<your-acs-endpoint>.communication.azure.com/;accesskey=<your-access-key>",
    "AzureOpenAI": "<your-openai-api-key>",
    "AzureOpenAIEndPoint": "https://<your-openai-endpoint>/openai/realtime?api-version=2024-10-01-preview",
    "UriHost": "https://<your-dev-tunnel-or-localhost-url>/",
    "CallerPhoneNumber": "<your-verified-phone-number>",
    "TargetPhoneNumber": "<recipient-phone-number>",
    "RecordingsBlobStorageContainer": "https://<your-storage-account-name>.blob.core.windows.net/<your-container-name>"
  }
}
```

## License

This project is licensed under the MIT License.
