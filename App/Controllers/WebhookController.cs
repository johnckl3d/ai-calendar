using Azure;
using Azure.AI.Agents.Persistent;
using Azure.AI.Projects;
using Azure.AI.Projects.OpenAI;
using Azure.Communication.Messages;
using Azure.Core;
using Azure.Identity;
using Azure.Messaging.EventGrid;
using Azure.Messaging.EventGrid.SystemEvents;
using CpmDemoApp.Models;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;
using System.Text;
using System.Text.Json;

#pragma warning disable OPENAI001
namespace viewer.Controllers
{
    [Route("webhook")]
    public class WebhookController : Controller
    {
        private static bool _clientsInitialized;
        private static NotificationMessagesClient _notificationMessagesClient;
        private static Guid _channelRegistrationId;

        private static PersistentAgentsClient _persistentAgentsClient;
        private static string _deploymentName;
        private static string _endpointURL;
        private static string _agentId;
        private static string _agentVersion;
        private static string _tenantId;
        private static string _clientId;
        private static string _secret;
        private static string _key;
        private static PersistentAgentThread _thread;
        private static AIProjectClient _projectClient;
        private static ProjectConversation _conversation;
        private static AgentReference _agentReference;
        private readonly TelemetryClient _telemetryClient;


        private static string SystemPrompt => "You are an AI agent whose only source of information is the AI Search Knowledge Base, which consists exclusively of the documents that have been uploaded to it.Do not use any external knowledge, assumptions, or previous training data when responding to questions. Only reference and cite the relevant information found strictly in the provided document data.If you cannot find the answer within the uploaded documents, reply with:\r\n\"I’m sorry, but I can only provide answers based on the information found in the AI Search Knowledge Base. I could not find this information in the documents provided";

        private bool EventTypeSubcriptionValidation
            => HttpContext.Request.Headers["aeg-event-type"].FirstOrDefault() ==
               "SubscriptionValidation";

        private bool EventTypeNotification
            => HttpContext.Request.Headers["aeg-event-type"].FirstOrDefault() ==
               "Notification";

        private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

        public WebhookController(
            IOptions<NotificationMessagesClientOptions> notificationOptions,
            IOptions<CpmDemoApp.Models.OpenAIClientOptions> AIOptions,
            IOptions<TenantOptions> TenantOptions,
             IOptions<AzureSearchOptions> AzureSearchOptions,
            TelemetryClient telemetryClient)
        {
            _telemetryClient = telemetryClient;
            _telemetryClient.TrackTrace("CalendarApp:WebhookController::Init");
            if (!_clientsInitialized)
            {
                _telemetryClient.TrackTrace("CalendarApp:WebhookController::Init::client not initialized");
                _channelRegistrationId = Guid.Parse(notificationOptions.Value.ChannelRegistrationId);
                _deploymentName = AIOptions.Value.DeploymentName;

                _key = AIOptions.Value.Key;
                _endpointURL = AIOptions.Value.Endpoint;
                _agentId = AIOptions.Value.AgentId;
                _tenantId = TenantOptions.Value.TenantId;
                _clientId = TenantOptions.Value.ClientId;
                _secret = TenantOptions.Value.Secret;
                _telemetryClient.TrackTrace("CalendarApp:WebhookController::Init::ConnectionString::" + notificationOptions.Value.ConnectionString);
                _notificationMessagesClient = new NotificationMessagesClient(notificationOptions.Value.ConnectionString);

              

                _telemetryClient.TrackTrace("CalendarApp:WebhookController::Init::_deploymentName::" + _deploymentName);
                _telemetryClient.TrackTrace("CalendarApp:WebhookController::Init::_endpointURL::" + _endpointURL);
                _telemetryClient.TrackTrace("CalendarApp:WebhookController::Init::_agentId::" + _agentId);
                _telemetryClient.TrackTrace("CalendarApp:WebhookController::Init::_tenantId::" + _tenantId);
                _telemetryClient.TrackTrace("CalendarApp:WebhookController::Init::_clientId::" + _clientId);
                _telemetryClient.TrackTrace("CalendarApp:WebhookController::Init::_secret::" + _secret);
                try
                {
                    TokenCredential credential = BuildCredential();

                    _projectClient = new(endpoint: new Uri(_endpointURL), tokenProvider: credential);
                    _conversation = _projectClient.OpenAI.Conversations.CreateProjectConversation();
                    _agentReference = new AgentReference(name: _agentId, version: _agentVersion);

                    _clientsInitialized = true;
                }
                catch (Exception ex)
                {
                    _telemetryClient.TrackException(ex, new Dictionary<string, string?>
                    {
                        ["Operation"] = "InitializeClients"
                    });
                    throw;
                }
            }
        }

        [HttpOptions]
        public async Task<IActionResult> Options()
        {
            using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
            {
                var webhookRequestOrigin = HttpContext.Request.Headers["WebHook-Request-Origin"].FirstOrDefault();
                var webhookRequestCallback = HttpContext.Request.Headers["WebHook-Request-Callback"];
                var webhookRequestRate = HttpContext.Request.Headers["WebHook-Request-Rate"];
                HttpContext.Response.Headers.Add("WebHook-Allowed-Rate", "*");
                HttpContext.Response.Headers.Add("WebHook-Allowed-Origin", webhookRequestOrigin);
            }

            return Ok();
        }

        [HttpPost]
        public async Task<IActionResult> Post()
        {
            using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
            {
                _telemetryClient.TrackTrace("CalendarApp:WebhookController::Post");
                var jsonContent = await reader.ReadToEndAsync();

                // Check the event type.
                // Return the validation code if it's a subscription validation request. 
                if (EventTypeSubcriptionValidation)
                {
                    return await HandleValidation(jsonContent);
                }
                else if (EventTypeNotification)
                {
                    _telemetryClient.TrackTrace("CalendarApp:WebhookController::Post::EventTypeNotification");
                    return await HandleGridEvents(jsonContent);
                }

                return BadRequest();
            }
        }

        private async Task<JsonResult> HandleValidation(string jsonContent)
        {
            var eventGridEvent = JsonSerializer.Deserialize<EventGridEvent[]>(jsonContent, _jsonOptions).First();
            var eventData = JsonSerializer.Deserialize<SubscriptionValidationEventData>(eventGridEvent.Data.ToString(), _jsonOptions);
            var responseData = new SubscriptionValidationResponse
            {
                ValidationResponse = eventData.ValidationCode
            };
            return new JsonResult(responseData);
        }

        private async Task<IActionResult> HandleGridEvents(string jsonContent)
        {
            _telemetryClient.TrackTrace("CalendarApp:WebhookController::Handling Event Grid events for WhatsApp webhook.");
            _telemetryClient.TrackTrace("CalendarApp:WebhookController::HandleGridEvents::jsonContent::" + jsonContent);
            var eventGridEvents = JsonSerializer.Deserialize<EventGridEvent[]>(jsonContent, _jsonOptions);

            foreach (var eventGridEvent in eventGridEvents)
            {
                if (eventGridEvent.EventType.Equals("microsoft.communication.advancedmessagereceived", StringComparison.OrdinalIgnoreCase))
                {
                    var messageData = JsonSerializer.Deserialize<AdvancedMessageReceivedEventData>(eventGridEvent.Data.ToString(), _jsonOptions);
                    Messages.MessagesListStatic.Add(new Message
                    {
                        Text = $"Customer({messageData.From}): \"{messageData.Content}\""
                    });
                    _telemetryClient.TrackTrace("CalendarApp:WebhookController:HandleGridEvents:messageData.Content::" + messageData.Content);
                    //Messages.OpenAIConversationHistory.Add(new UserChatMessage(messageData.Content));
                    await RespondToCustomerAsync(messageData.From, messageData.Content);
                }
            }

            return Ok();
        }

        private async Task RespondToCustomerAsync(string numberToRespondTo, string customerMessage)
        {
            _telemetryClient.TrackTrace("CalendarApp:WebhookController:RespondToCustomerAsync");
            _telemetryClient.TrackTrace("CalendarApp:WebhookController:RespondToCustomerAsync:numberToRespondTo::" + numberToRespondTo);
            try
            {
                var assistantResponseText = await GenerateAIResponseAsync(customerMessage);
                _telemetryClient.TrackTrace("CalendarApp:WebhookController:RespondToCustomerAsync:assistantResponseText::" + assistantResponseText);
                if (string.IsNullOrWhiteSpace(assistantResponseText))
                {
                    Messages.MessagesListStatic.Add(new Message
                    {
                        Text = "Error: No response generated from Azure OpenAI."
                    });
                    return;
                }

                await SendWhatsAppMessageAsync(numberToRespondTo, assistantResponseText);
                _telemetryClient.TrackTrace("CalendarApp:WebhookController:RespondToCustomerAsync:assistantResponseText::after SendWhatsAppMessageAsync");
                //Messages.OpenAIConversationHistory.Add(new AssistantChatMessage(assistantResponseText));
                Messages.MessagesListStatic.Add(new Message
                {
                    Text = $"Assistant: {assistantResponseText}"
                });
            }
            catch (RequestFailedException e)
            {
                // Track exception to Application Insights
                _telemetryClient.TrackException(e, new Dictionary<string, string?>
                {
                    ["Operation"] = "RespondToCustomerAsync",
                    ["CustomerNumber"] = numberToRespondTo
                });

                Messages.MessagesListStatic.Add(new Message
                {
                    Text = $"CalendarApp:WebhookController:RespondToCustomerAsync:Error: Failed to respond to \"{numberToRespondTo}\". Exception: {e.Message}"
                });
            }
        }


        private async Task<string?> GenerateAIResponseAsync(string customerMessage)
        {
            try
            {
                _telemetryClient.TrackTrace("CalendarApp:WebhookController:GenerateAIResponseAsync");
             
                ProjectResponsesClient responseClient = _projectClient.OpenAI.GetProjectResponsesClientForAgent(_agentReference, _conversation.Id);
                _telemetryClient.TrackTrace("CalendarApp:WebhookController:GenerateAIResponseAsync:customerMessage:" + customerMessage);
                ResponseResult response = responseClient.CreateResponse(customerMessage);

                // Example – adjust to your actual API surface:
                //string resultText = response.GetOutputText()
                //                  ?? response.ToString(); // fallback if no OutputText
                //_telemetryClient.TrackTrace("CalendarApp:WebhookController:GenerateAIResponseAsync:response:" + resultText);

                //response.GetOutputText()

                // Aggregate all output items into a single string, preserving multi-message structure
                var textSegments = new List<string>();

                if (response.OutputItems is not null)
                {
                    IEnumerable<string> enumerable = (from item in response.OutputItems
                                                      where item is MessageResponseItem
                                                      select item as MessageResponseItem).SelectMany((MessageResponseItem message) => from contentPart in message.Content
                                                                                                                                      where contentPart.Kind == ResponseContentPartKind.OutputText
                                                                                                                                      select contentPart into outputTextPart
                                                                                                                                      select outputTextPart.Text);
                    if (!enumerable.Any())
                        if (!enumerable.Any())
                    {
                        return "no response";
                    }
                    // Create a single string with a new line per item
                    string resultText = string.Join(Environment.NewLine, enumerable);

                    _telemetryClient.TrackTrace("CalendarApp:WebhookController:GenerateAIResponseAsync:response:" + resultText);

                    return string.IsNullOrWhiteSpace(resultText) ? "no response" : resultText;
                }
                //    // Combine all text parts from the last assistant message
                //    var sb = new StringBuilder();

                //    foreach (var part in lastAssistantMessage.ContentItems)
                //    {
                //        if (part is MessageTextContent t)
                //        {
                //            sb.AppendLine(t.Text);
                //        }
                //        else
                //        {
                //            sb.AppendLine(part.ToString());
                //        }
                //    }

                //    var result = sb.ToString().Trim();
                //    _telemetryClient.TrackTrace("CalendarApp:WebhookController:GenerateAIResponseAsync:finalResponse::" + result);
                //    return string.IsNullOrEmpty(result) ? "no response" : result;
            }
            catch (Exception ex)
            {
                _telemetryClient.TrackTrace("CalendarApp:WebhookController:GenerateAIResponseAsync:error::" + ex.Message);
                _telemetryClient.TrackException(ex, new Dictionary<string, string?>
                {
                    ["Operation"] = "GenerateAIResponseAsync"
                });
                throw;
            }
            return null;
        }


        private async Task SendWhatsAppMessageAsync(string numberToRespondTo, string message)
        {
            var recipientList = new List<string> { numberToRespondTo };
            var textContent = new TextNotificationContent(_channelRegistrationId, recipientList, message);
            await _notificationMessagesClient.SendAsync(textContent);
        }


        static TokenCredential BuildCredential()
        {
            // Prefer service principal if all three vars are present

            //if (!string.IsNullOrWhiteSpace(_tenantId) &&
            //    !string.IsNullOrWhiteSpace(_clientId) &&
            //    !string.IsNullOrWhiteSpace(_secret))
            //{
            //    return new ClientSecretCredential(_tenantId, _clientId, _secret);
            //}
            return new ManagedIdentityCredential();
            // Otherwise, use dev chain and ignore partial/bad env creds
            return new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ExcludeEnvironmentCredential = true,
                ExcludeManagedIdentityCredential = true // set false if running in Azure with MSI
            });
        }
    }
}