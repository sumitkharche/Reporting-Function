using Azure.Identity;
using Azure.Messaging.ServiceBus;
using FFF_Reporting_AzureFunction.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;
namespace FFF.Reporting
{
    public class SendReportingEmailFunction
    {
        private readonly ILogger<SendReportingEmailFunction> _logger;
        private readonly string _clientId;
        private readonly string _clientSecret;
        //private readonly string _scope;
        private readonly string _tenantId;
        private readonly string _tokenBaseUrl;
        private readonly int SLEEP_TIME = 2000;
        private readonly string exportFormat = "XSLX";

        public SendReportingEmailFunction(ILogger<SendReportingEmailFunction> logger)
        {
            _logger = logger;
            _clientId = Environment.GetEnvironmentVariable("ClientId") ?? "";
            _clientSecret = Environment.GetEnvironmentVariable("ClientSecret") ?? "";
            //_scope = Convert.ToBoolean(Environment.GetEnvironmentVariable("UsePowerAutomateAPI")) ? Environment.GetEnvironmentVariable("PowerAutomateScope") ?? "" : Environment.GetEnvironmentVariable("PowerBIScope") ?? "";
            _tenantId = Environment.GetEnvironmentVariable("TenentId") ?? "";
            _tokenBaseUrl = Environment.GetEnvironmentVariable("TokenBaseUrl") ?? "";
        }

        [Function(nameof(SendReportingEmailFunction))]
        public async Task Run(
            [ServiceBusTrigger("%ServiceBusQueueName%", Connection = "ServiceBusConnection")]
            ServiceBusReceivedMessage message,
            ServiceBusMessageActions messageActions)
        {
            _logger.LogInformation($"{nameof(SendReportingEmailFunction)} requested.");
            _logger.LogInformation("Message ID: {id}", message.MessageId);
            _logger.LogInformation($"{nameof(SendReportingEmailFunction)} - Request Body captured: {message.Body}");
            var reportingRequest = message.Body.ToString();         
            await SendReportingEmail(reportingRequest);

            // Complete the message
            await messageActions.CompleteMessageAsync(message);
        }

        private async Task SendReportingEmail(string reportingRequest)
        {
            bool isMockAPIEnabled = Convert.ToBoolean(Environment.GetEnvironmentVariable("EnableMockAPI"));
            _logger.LogInformation($"{nameof(SendReportingEmailFunction)}- IsMockAPIEnabled {isMockAPIEnabled}");
            if (isMockAPIEnabled)
            {
                _logger.LogInformation("Calling Mock API");
                var req = JsonConvert.DeserializeObject<ReportingRequest>(reportingRequest);
                await ProcessMockAPI(req);
            }
            else
            {
                int delayInSec = Convert.ToInt16(Environment.GetEnvironmentVariable("DelayInSeconds"));
                _logger.LogInformation($"Delay Started at {DateTime.Now}");
                await Task.Delay(TimeSpan.FromSeconds(delayInSec));
                _logger.LogInformation($"Delay Completed at {DateTime.Now}");
                bool usePowerAutomateAPI = Convert.ToBoolean(Environment.GetEnvironmentVariable("UsePowerAutomateAPI"));
                if (usePowerAutomateAPI)
                {
                    _logger.LogInformation($"Calling PowerAutomate API with Request: {reportingRequest}");
                    await SendReportingEmailUsingPowerAutomateAsync(reportingRequest);
                }
                else
                {
                    _logger.LogInformation($"Calling PowerBI API with Request: {reportingRequest}");
                    var req = JsonConvert.DeserializeObject<ReportingRequest>(reportingRequest);
                    await SendReportingEmailUsingPowerBIAPIAsync(req);
                }
            }
        }
        private async Task ProcessMockAPI(ReportingRequest reportingRequest)
        {
            try
            {
                using (HttpClient httpClient = new HttpClient())
                {
                    var mockApiUrl = Environment.GetEnvironmentVariable("MockApiUrl");
                    _logger.LogInformation($"{nameof(SendReportingEmailFunction)}- MockAPIURL: {mockApiUrl}");
                    var requestContent = new StringContent(JObject.FromObject(reportingRequest).ToString(), Encoding.UTF8, "application/json");
                    var mockApiSubscriptionKey = Environment.GetEnvironmentVariable("MockApiSubscriptionKey");
                    httpClient.DefaultRequestHeaders.Add("ocp-apim-subscription-key", mockApiSubscriptionKey);
                    var response = await httpClient.PostAsync(mockApiUrl, requestContent);
                    _logger.LogInformation($"Mock API Response status: {response.StatusCode}");

                    if (response.IsSuccessStatusCode)
                    {
                        var responseMessage = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        _logger.LogInformation($"Mock API response: {responseMessage}");
                    }
                    else
                    {
                        _logger.LogInformation($"Error occurred while calling the Mock API StatusCode: {response.StatusCode} RequestBody: {reportingRequest}");
                        throw new Exception($"Exception- Error occurred while calling the Mock API for StatusCode: {response.StatusCode} RequestBody: {reportingRequest}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error while processing mock api message: {ex.Message}");
                throw;
            }
        }

        private AccessTokenDetails? GetAccessToken(string scope)
        {
            var values = new Dictionary<string, string>
            {
                {"client_id",$"{this._clientId}" },
                {"client_secret",$"{this._clientSecret}" },
                {"scope",$"{scope}" },
                {"grant_type","client_credentials" }
            };

            using var data = new FormUrlEncodedContent(values);
            var url = $"{this._tokenBaseUrl}{this._tenantId}/oauth2/v2.0/token";
            _logger.LogInformation("Getting access token");
            using var client = new HttpClient();
            var response = client.PostAsync(url, data);
            string result = response.Result.Content.ReadAsStringAsync().Result;
            var accessTokenDetails = JsonConvert.DeserializeObject(result, typeof(AccessTokenDetails)) as AccessTokenDetails;
            _logger.LogInformation("Access token fetch successfully ");
            return accessTokenDetails;
        }


        private async Task SendReportingEmailUsingPowerAutomateAsync(string reportingRequest)
        {
            try
            {
                using (HttpClient httpClient = new HttpClient())
                {
                    var powerBIApiUrl = Environment.GetEnvironmentVariable("PowerAutomateApiUrl");
                    var scope = Environment.GetEnvironmentVariable("PowerAutomateScope") ?? "";
                    var reportingRequestObj = JsonConvert.DeserializeObject<Dictionary<string, object>>(reportingRequest) ?? [];
                    string reportType = (string)reportingRequestObj["reportType"];
                    powerBIApiUrl += $"&report_type={reportType}";
                    var requestContent = new StringContent(reportingRequest, Encoding.UTF8, "application/json");
                    var tokenDetails = GetAccessToken(scope);
                    httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {tokenDetails?.AccessToken}");
                    _logger.LogInformation($"Calling PowerAutomate API with request body {reportingRequest}");
                    var response = await httpClient.PostAsync(powerBIApiUrl, requestContent);
                    _logger.LogInformation("PowerAutomate API Response status: {0} for reportId :{1}", response.StatusCode, reportingRequestObj["reportId"]);
                    if (response.IsSuccessStatusCode)
                    {
                        var responseMessage = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        _logger.LogInformation("PowerAutomateAPI response: {0}", responseMessage);
                    }
                    else
                    {
                        _logger.LogInformation($"Error occurred while calling the PowerAutomate API StatusCode: {response.StatusCode} RequestBody: {reportingRequest}");
                        throw new Exception($"Exception- Error occurred while calling the PowerAutomate API StatusCode: {response.StatusCode} RequestBody: {reportingRequest}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Exception occurred while processing PowerAutomate API Exception: {ex.Message} RequestBody: {reportingRequest}");
                throw;
            }
        }

        private async Task SendReportingEmailUsingPowerBIAPIAsync(ReportingRequest reportingRequest)
        {
            string reportType = reportingRequest.ReportType;
            var requestContent = BuildReportingRequest(reportType, reportingRequest);
            string groupId = Environment.GetEnvironmentVariable("PowerBIGroupId") ?? "";
            string reportId = "";
            string initialFileName = "";
            var scope = Environment.GetEnvironmentVariable("PowerBIScope") ?? "";
            int retryCount = 0;
            switch (reportType)
            {
                case "invoices":
                    reportId = Environment.GetEnvironmentVariable("InvoiceReportId") ?? "";
                    initialFileName = "Invoice History Report";
                    break;
                case "orders":
                    reportId = Environment.GetEnvironmentVariable("OrdersReportId") ?? "";
                    initialFileName = "Order History Report";
                    break;
                case "allocations":
                    reportId = Environment.GetEnvironmentVariable("AllocationsReportId") ?? "";
                    initialFileName = "Allocations Report";
                    break;
                default:
                    break;
            }
            try
            {
                using (HttpClient httpClient = new HttpClient())
                {
                    var powerBIApiUrl = Environment.GetEnvironmentVariable("PowerBIApiBaseUrl");
                    var exportApiUrl = powerBIApiUrl + $"{groupId}" + "/reports/" + $"{reportId}" + "/ExportTo";
                    var tokenDetails = GetAccessToken(scope);
                    httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {tokenDetails?.AccessToken}");
                    string jsonReqBody = JsonConvert.SerializeObject(requestContent);
                    var content = new StringContent(jsonReqBody, Encoding.UTF8, "application/json");
                    var response = await httpClient.PostAsync(exportApiUrl, content);
                    _logger.LogInformation($"PowerBI API Response status:{response.StatusCode}");

                    if (response.IsSuccessStatusCode)
                    {
                        var responseMessage = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        _logger.LogInformation("PowerBI Export API response: {0}", responseMessage);

                        dynamic responseJson = JObject.Parse(responseMessage);
                        string exportId = responseJson.id;
                        var powerBIBaseApiUrl = Environment.GetEnvironmentVariable("PowerBIApiBaseUrl");
                        string statusUrl = powerBIBaseApiUrl + $"{groupId}" + "/reports/" + $"{reportId}" + "/exports/" + $"{exportId}";
                        string jobStatus = "Running";                        
                        while (jobStatus == "Running")
                        {
                            retryCount++;
                            _logger.LogInformation($"Retry Count: {retryCount}");
                            _logger.LogInformation($"Getting Export Status - Delay Started");
                            await Task.Delay(10000);
                            _logger.LogInformation($"Getting Export Status - Delay Completed");
                            try
                            {
                                 _logger.LogInformation($"Calling Export Status API");
                                using HttpClient newHttpClient = new HttpClient();
                                newHttpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {tokenDetails?.AccessToken}");
                                var statusResponse = await newHttpClient.GetAsync(statusUrl);
                                statusResponse.EnsureSuccessStatusCode();
                                var statusContent = await statusResponse.Content.ReadAsStringAsync();
                                responseJson = JObject.Parse(statusContent);
                                jobStatus = responseJson.status;
                                _logger.LogInformation($"Export Status API response StatusCode:{statusResponse.StatusCode} JobStatus:{jobStatus} ");
                                _logger.LogInformation($"Current JobStatus : {jobStatus}");
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError($"Error checking export job status error message: {ex.Message} JobStatus : {jobStatus}");

                            }
                        }

                        // Download the exported file once the job is complete
                        if (jobStatus == "Succeeded")
                        {
                            string downloadUrl = responseJson?.resourceLocation ?? "";

                            try
                            {
                                 _logger.LogInformation($"Calling PowerBI API to download file with URL:{downloadUrl}"); 
                                var fileResponse = await httpClient.GetAsync(downloadUrl);
                                fileResponse.EnsureSuccessStatusCode();
                                var fileBytes = await fileResponse.Content.ReadAsByteArrayAsync();
                                _logger.LogInformation($"Report Exported Successfully ReportType:{reportType}");
                                await ProcessEmailRequestAsync(reportType, initialFileName, fileBytes, reportingRequest);

                            }
                            catch (Exception ex)
                            {
                                _logger.LogError($"Error downloading Report. ReportId:{reportingRequest.ReportId} Report Type: {reportType} exported file url:{downloadUrl} Error Message: {ex.Message}");
                            }
                        }
                        else
                        {
                            _logger.LogError($"Export job failed for ReportId:{reportingRequest.ReportId} Report: {reportType} with status: {jobStatus}");
                        }

                    }
                    else
                    {
                        _logger.LogInformation($"Error occurred for ReportId:{reportingRequest.ReportId} Report Type: {reportType} while calling the Power BI API StatusCode: {response.StatusCode} RequestBody: {jsonReqBody}");
                        //throw new Exception($"Exception- Error occurred while calling the PowerBI API StatusCode: {response.StatusCode} RequestBody: {jsonReqBody}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Exception occurred forReport Type: {reportType} and ReportId: {reportingRequest.ReportId} while processing PowerBI API Exception: {ex.Message} RequestBody: {reportingRequest}");
                //throw;
            }
        }

        private PowerBIReportRequest BuildReportingRequest(string reportType, ReportingRequest reportingRequest)
        {
            var parameterValueData = new List<ParameterValue>();
            if (reportType != null && reportType == "allocations")
            {
                if (reportingRequest != null && reportingRequest.Accounts?.Count > 0)
                {
                    foreach (var account in reportingRequest.Accounts)
                    {
                        var parameterObject = new ParameterValue
                        {
                            Name = "EcommerceAllocationsvwCustomerAccount",
                            Value = account.AccountId
                        };
                        parameterValueData.Add(parameterObject);
                    }
                }

                if (reportingRequest != null && reportingRequest.Products?.Count > 0)
                {
                    foreach (var product in reportingRequest.Products)
                    {
                        var parameterObject = new ParameterValue
                        {
                            Name = "EcommerceAllocationsvwItemNumber",
                            Value = product.Sku
                        };
                        parameterValueData.Add(parameterObject);
                    }
                }

            }

            if (reportType != null && reportType == "invoices")
            {
                if (reportingRequest != null && reportingRequest.Accounts?.Count > 0)
                {
                    foreach (var account in reportingRequest.Accounts)
                    {
                        var parameterObject = new ParameterValue
                        {
                            Name = "EcommerceInvoiceHistoryvwCustomerAccount",
                            Value = account.AccountId
                        };
                        parameterValueData.Add(parameterObject);
                    }
                }

                if (reportingRequest != null && reportingRequest.StartDate != null)
                {
                    var parameterObject = new ParameterValue
                    {
                        Name = "FromEcommerceInvoiceHistoryvwInvoiceDate",
                        Value = reportingRequest.StartDate
                    };
                    parameterValueData.Add(parameterObject);
                }
                if (reportingRequest != null && reportingRequest.EndDate != null)
                {
                    var parameterObject = new ParameterValue
                    {
                        Name = "ToEcommerceInvoiceHistoryvwInvoiceDate",
                        Value = reportingRequest.EndDate
                    };
                    parameterValueData.Add(parameterObject);
                }
                if (reportingRequest != null && reportingRequest.Status != null)
                {
                    var statuses = reportingRequest.Status?.ToString().Split(',');
                    if (statuses != null && statuses?.Length > 0)
                    {
                        foreach (var status in statuses)
                        {
                            var parameterObject = new ParameterValue
                            {
                                Name = "EcommerceInvoiceHistoryvwInvoiceStatus",
                                Value = status.Trim()
                            };
                            parameterValueData.Add(parameterObject);
                        }
                    }
                }
            }

            if (reportType != null && reportType == "orders")
            {
                if (reportingRequest != null && reportingRequest.Accounts?.Count > 0)
                {
                    foreach (var account in reportingRequest.Accounts)
                    {
                        var parameterObject = new ParameterValue
                        {
                            Name = "EcommerceOrderHistoryvwCustomerAccount",
                            Value = account.AccountId
                        };
                        parameterValueData.Add(parameterObject);
                    }
                }

                if (reportingRequest != null && reportingRequest.StartDate != null)
                {
                    var parameterObject = new ParameterValue
                    {
                        Name = "FromEcommerceOrderHistoryvwOrderdate",
                        Value = reportingRequest.StartDate
                    };
                    parameterValueData.Add(parameterObject);
                }
                if (reportingRequest != null && reportingRequest.EndDate != null)
                {
                    var parameterObject = new ParameterValue
                    {
                        Name = "ToEcommerceOrderHistoryvwOrderdate",
                        Value = reportingRequest.EndDate
                    };
                    parameterValueData.Add(parameterObject);
                }
                if (reportingRequest != null && reportingRequest.Status != null)
                {
                    var statuses = reportingRequest.Status?.ToString().Split(',');
                    if (statuses != null && statuses?.Length > 0)
                    {
                        foreach (var status in statuses)
                        {
                            var parameterObject = new ParameterValue
                            {
                                Name = "EcommerceOrderHistoryvwHeaderOrderStatus",
                                Value = status.Trim()
                            };
                            parameterValueData.Add(parameterObject);
                        }
                    }
                }
            }

            var paginatedReportConfiguration = new PaginatedReportConfiguration
            {
                ParameterValues = parameterValueData
            };

            var reportRequest = new PowerBIReportRequest
            {
                Format = "xlsx",
                PaginatedReportConfiguration = paginatedReportConfiguration
            };
            return reportRequest;
        }

        private async Task ProcessEmailRequestAsync(string reportType,string fileName, byte[] fileData, ReportingRequest reportingRequest)
        {
            foreach (var data in reportingRequest?.EmailIds)
            {
                _logger.LogInformation($"Sending email to emailId:{data.EmailId} for reportType:{reportType} ReportId:{reportingRequest.ReportId} ");
                await SendEmailAsync(reportType,fileName, data.EmailId, reportingRequest.ReportId, fileData);
            }
        }

        private async Task SendEmailAsync(string reportType, string fileName, string emailId, string reportId, byte[] fileData)
        {
            string fromEmail = Environment.GetEnvironmentVariable("FromEmailAddress") ?? "";
            string emailBody = $@"
                                <html>
                                <body>
                                    <p>Thank you for choosing FFF Enterprises!</p>           
                                    <p>Please find attached the {fileName} you requested.</p>
                                    <p>Please do not reply to this email. This email is from a non-monitored mailbox. For questions, please contact fffcustomercare@fffenterprises.com or your FFF Sales Representative by calling 800-843-7477 </p>
                                </body>
                                </html>";

            var requestBody = new SendMailPostRequestBody
            {
                Message = new Message
                {
                    Subject = $"{fileName}",
                    Body = new ItemBody
                    {
                        ContentType = BodyType.Html,
                        Content = emailBody,
                    },
                    ToRecipients = new List<Recipient>
                    {
                        new Recipient
                        {
                            EmailAddress = new EmailAddress
                            {
                                Address = emailId,
                            },
                        },
                    },
                    Attachments = new List<Microsoft.Graph.Models.Attachment>()
                    {
                        new FileAttachment()
                           {
                                OdataType = "#microsoft.graph.fileAttachment",
                                Name = $"{fileName}.xlsx",
                                ContentBytes = fileData,
                                ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
                           }
                    }
                },
                SaveToSentItems = false,
            };
            try
            {
                var graphClient = GetGraphClient();
                await graphClient.Users[fromEmail].SendMail.PostAsync(requestBody);
                _logger.LogInformation($"Email send successfully. EmailId:{emailId} ReportType:{reportType} ReportId:{reportId}");
            }
            catch (ServiceException ex)
            {
                // Handle Microsoft Graph API errors
                _logger.LogError($"Email Error: Error sending email for emailId:{emailId} ReportType:{reportType} ReportId:{reportId} Error Message: {ex.Message} Error code: {ex.ResponseStatusCode} Error Stacktrace details: {ex.StackTrace}");
            }
            catch (Exception ex)
            {
                // Handle other exceptions
                _logger.LogError($"Unexpected error sending email for emailId:{emailId} ReportType:{reportType} ReportId:{reportId} Error Message: {ex.Message}");
            }
        }

        private GraphServiceClient GetGraphClient()
        {
            string scope = Environment.GetEnvironmentVariable("GraphAPIScope") ?? "";
            string[] scopes = new string[] { scope };

            var options = new ClientSecretCredentialOptions
            {
                AuthorityHost = AzureAuthorityHosts.AzurePublicCloud
            };

            var clientSecretCredential = new ClientSecretCredential(this._tenantId, this._clientId, this._clientSecret, options);

            GraphServiceClient graphClient = new GraphServiceClient(clientSecretCredential, scopes);

            return graphClient;
        }


    }
}
