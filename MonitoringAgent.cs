using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Net;
using System.Net.Http;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text;
using System.Linq;
using System.Dynamic;
using System.Text.RegularExpressions;
using System.Globalization;
using Newtonsoft.Json;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace MonitoringAgent
{
    public static class MonitoringAgent
    {
        [Function("MonitoringAgent")]
        public static async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequestData req,
            FunctionContext executionContext)
        {

            var logger = executionContext.GetLogger("MonitoringAgent");
            logger.LogInformation("C# HTTP trigger function processed a request.");

            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);

            FlexibleAssetAPIResponse lastRunAssets = await GetLastRunAssets();
            FlexibleAssetData[] data = lastRunAssets.data;
            List<Warning> warnings = new List<Warning>();

            // Loop through each organization's lastRunAsset
            foreach (FlexibleAssetData lastRunAsset in data)
            {
                int id;
                try
                {
                    id = Int32.Parse(lastRunAsset.id);
                }
                catch (FormatException)
                {
                    Console.Error.WriteLine($"Unable to parse asset id: '{lastRunAsset.id}'");
                    continue;
                }

                int orgID = lastRunAsset.attributes["organization-id"];
                string orgName = lastRunAsset.attributes["organization-name"];
                var currentDate = TimeZoneInfo.ConvertTime (DateTime.Now,
                            TimeZoneInfo.FindSystemTimeZoneById(Constants.TimeZone));
                Console.WriteLine($"Checking: {orgName} (asset: {id})");

                // Script Checks: loop through each check in the lastRunChecks dictionary and look for issues
                foreach (var audit in Constants.lastRunChecks)
                {
                    // Test if property exists on last run page (if not, its never been set and we can ignore it)
                    string testingTrait;
                    if (lastRunAsset.attributes.traits.ContainsKey(audit.Key)) 
                    {
                        testingTrait = lastRunAsset.attributes.traits[audit.Key];
                    } 
                    else 
                    {
                        continue;
                    }


                    // Test if monitoring is disabled for this trait, if so ignore it
                    string key = $"{audit.Key}-monitoring-disabled";
                    if (lastRunAsset.attributes.traits.ContainsKey(key) && lastRunAsset.attributes.traits[key] == true) {
                        continue;
                    }
                    
                    var auditTest = audit.Value;
                    if (auditTest.ContainsKey("latest-version"))
                    {
                        // Version check
                        string parsedTest = Regex.Replace(testingTrait, @"[^\d|\.]", "");
                        Version testVersion;

                        if (Version.TryParse(parsedTest, out testVersion)) {}
                        else
                        {
                            Console.Error.Write($"Not a proper version: {parsedTest} (trait: {audit.Key}) (org: {orgID})");
                            continue;
                        }

                        Version latestVersion;
                        Version.TryParse(auditTest["latest-version"], out latestVersion);
                        DateTime versionUpdatedOn = DateTime.ParseExact(auditTest["version-updated-on"], "dd/MM/yyyy", CultureInfo.InvariantCulture);

                        // Find the last time the User Audit script ran
                        var allUATests = Constants.lastRunChecks.Where(x => x.Value.Any(y => y.Value.Contains("[User Audit]"))).Select(x => x.Key).ToList<string>();
                        List<DateTime> lastUpdatedUADates = new List<DateTime>();
                        foreach (string trait in allUATests)
                        {
                            if (!lastRunAsset.attributes.traits.ContainsKey(trait))
                            {
                                continue;
                            }

                            DateTime lastRunDate;
                            try
                            {
                                    string date = lastRunAsset.attributes.traits[trait];
                                    lastRunDate = DateTime.Parse(date);
                            }
                            catch
                            {
                                continue;
                            }
                            lastUpdatedUADates.Add(lastRunDate);
                        }

                        DateTime lastUpdatedUA = DateTime.MinValue;
                        bool updatesFound = false;
                        if (lastUpdatedUADates.Count > 0)
                        {
                            lastUpdatedUA = lastUpdatedUADates.OrderByDescending(x => x).FirstOrDefault();
                            updatesFound = true;
                        }

                        if (latestVersion != testVersion && updatesFound && lastUpdatedUA > versionUpdatedOn)
                        {
                            warnings.Add(
                                BuildWarning(auditTest, lastRunAsset, latestVersion.ToString(), testVersion.ToString())
                            );
                        }
                    }
                    else if (auditTest.ContainsKey("inactive-days"))
                    {
                        // Inactive days check
                        DateTime testDate;
                        try
                        {
                            testDate = DateTime.Parse(testingTrait);
                        }
                        catch
                        {
                            // Not a date, this shouldn't happen unless grabbing the wrong field
                            Console.Error.Write($"Not a date: {testingTrait} (trait: {audit.Key}) (org: {orgID})");
                            continue;
                        }
                        int testDaysDiff;
                        try
                        {
                            testDaysDiff = Int32.Parse(auditTest["inactive-days"]);
                        }
                        catch
                        {
                            // A non-int value was written in the dictionary for inactive-days, this is incorrect!
                            Console.Error.Write($"Inactive-days is not an int: {0} (trait: {1}) (org: {2})", auditTest["inactive-days"], audit.Key, orgID);
                            continue;
                        }

                        int dayDifference = (currentDate - testDate).Days;

                        if (dayDifference > testDaysDiff)
                        {
                            warnings.Add(
                                BuildWarning(auditTest, lastRunAsset, testDaysDiff.ToString(), dayDifference.ToString())
                            );
                        }
                    }
                }

                // Custom Checks: loop through customLastRunChecks dictionary and look for issues, if any apply to this org
                if (Constants.customLastRunChecks.ContainsKey(orgID) && lastRunAsset.attributes.traits.ContainsKey("custom-scripts"))
                {
                    Dictionary<string, Dictionary<string, string>> orgsCustomChecks = Constants.customLastRunChecks[orgID];
                    string customScriptsTxt = lastRunAsset.attributes.traits["custom-scripts"];

                    foreach (var audit in orgsCustomChecks)
                    {
                        // Test if property exists in the Custom Scripts textbox on the last run page
                        DateTime testDate;
                        if (customScriptsTxt.Contains(audit.Key)) 
                        {
                            Regex regex = new Regex($@"{Regex.Escape(audit.Key)}:\s?(.+?)(<\/div>|$)");
                            MatchCollection matches = regex.Matches(customScriptsTxt);
                            List<DateTime> lastUpdates = new List<DateTime>();

                            foreach (Match match in matches)
                            {
                                GroupCollection groups = match.Groups;
                                string testDateString = groups[1].Value;
                                testDateString = Regex.Replace(testDateString, @"(\s|<br>|<br\s?\/>|\\n)+", "");
                                try 
                                {
                                    lastUpdates.Add(DateTime.Parse(testDateString));
                                }
                                catch
                                {
                                    Console.Error.Write($"Not a date: {groups[1].Value} (trait: {audit.Key}) (org: {orgID})");
                                    continue;
                                }
                            }

                            if (lastUpdates.Count < 1) {
                                continue;
                            }
                            testDate = lastUpdates.Max(x => x);
                        } 
                        else 
                        {
                            continue;
                        }

                        if (testDate == DateTime.MinValue)
                        {
                            continue;
                        }

                        // Run tests
                        var auditTest = audit.Value;
                        if (auditTest.ContainsKey("inactive-days"))
                        {
                            // Inactive days check
                            int testDaysDiff;
                            try
                            {
                                testDaysDiff = Int32.Parse(auditTest["inactive-days"]);
                            }
                            catch
                            {
                                // A non-int value was written in the dictionary for inactive-days, this is incorrect!
                                Console.Error.Write($"Inactive-days is not an int: {0} (trait: {1}) (org: {2})", auditTest["inactive-days"], audit.Key, orgID);
                                continue;
                            }

                            int dayDifference = (currentDate - testDate).Days;

                            if (dayDifference > testDaysDiff)
                            {
                                warnings.Add(
                                    BuildWarning(auditTest, lastRunAsset, testDaysDiff.ToString(), dayDifference.ToString())
                                );
                            }
                        }
                    }
                }
            }
           
            var response = req.CreateResponse(HttpStatusCode.OK);
            if (query["returnType"] != null && query["returnType"] == "html") 
            {
                string responseHTML = BuildResponseHTML(warnings);
                
                response.Headers.Add("Content-Type", "text/html");
                response.WriteString(responseHTML);
            }
            else if (query["returnType"] != null && query["returnType"] == "email")
            {
                if (warnings.Count > 0) 
                {
                    string emailSubject = $"The Monitoring Agent found {warnings.Count} Issues";
                    string emailIntro = $"The monitoring agent found {warnings.Count} issues that need to be resolved.";
                    string emailHTML = String.Format(Constants.emailTemplate, emailIntro, "Issues Found:", BuildResponseHTMLInner(warnings), "");
                    string[] toEmails = GetEnvironmentVariable("EMAILS_TO").Split(',');

                    var emailClient = new SendGridClient(GetEnvironmentVariable("SENDGRID_API_KEY"));
                    var newEmail = new SendGridMessage()
                    {
                        From = new EmailAddress(GetEnvironmentVariable("EMAIL_FROM_EMAIL"), GetEnvironmentVariable("EMAIL_FROM_NAME")),
                        Subject = emailSubject,
                        HtmlContent = emailHTML
                    };
                    foreach (string email in toEmails)
                    {
                        string parsedEmail = Regex.Replace(email, @"\s+", "");
                        newEmail.AddTo(new EmailAddress(parsedEmail, parsedEmail.Split('@')[0]));
                    }
                    var emailResponse = await emailClient.SendEmailAsync(newEmail).ConfigureAwait(false);
                }
            }
            else
            {
                string responseJson = System.Text.Json.JsonSerializer.Serialize(warnings);
                
                response.Headers.Add("Content-Type", "application/json");
                response.WriteString(responseJson);
            }


            return response;
        }

        /// <summary>
        /// Queries all of the "Scripts - Last Run" assets from IT Glue which we will parse for info on when scripts last ran
        /// </summary>
        private static async Task<FlexibleAssetAPIResponse> GetLastRunAssets()
        {
            HttpClient client = new HttpClient();
            client.BaseAddress = new Uri(GetEnvironmentVariable("ITG_API_URL")); 
            client.DefaultRequestHeaders.Add("x-api-key", GetEnvironmentVariable("ITG_API_KEY"));

            string uri = "flexible_assets/?filter[flexible_asset_type_id]=" + GetEnvironmentVariable("SCRIPTS_LAST_RUN_ASSET_TYPE_ID") + "&page[size]=200";

            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Content =  new StringContent(string.Empty, Encoding.UTF8, "application/vnd.api+json");
            
            HttpResponseMessage response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            
            string responseBody = await response.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<FlexibleAssetAPIResponse>(responseBody);
            
        }

        /// <summary>
        /// Returns an environment variable from Azure
        /// </summary>
        /// <param name="name">The name or key of the environment variable to grab.</param>
        /// <returns>The value of the environment variable.</returns>
        private static string GetEnvironmentVariable(string name)
        {
            return System.Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process);
        }

        /// <summary>
        /// Builds a warning object for when an issue is found
        /// </summary>
        /// <param name="auditTest">The test object from lastRunChecks or customLastRunChecks</param>
        /// <param name="lastRunAsset">The last run asset object from ITG, these are returned by GetLastRunAssets()</param>
        /// <param name="testVal">The value for the audit that we were expecting (e.g. the max amount of inactive days we are testing for)</param>
        /// <param name="currentVal">The value for the audit that we found (e.g. the actual amount of inactive days found)</param>
        /// <returns>A warning object</returns>
        private static Warning BuildWarning(Dictionary<string, string> auditTest, FlexibleAssetData lastRunAsset, string testVal, string currentVal)
        {
            string itgBaseURI = MonitoringAgent.GetEnvironmentVariable("ITG_BASE_URL");
            string orgID = lastRunAsset.attributes["organization-id"];

            Warning warning = new Warning
            {
                orgID = orgID,
                orgName = lastRunAsset.attributes["organization-name"],
                error = auditTest["error"],
                errorDetails = auditTest["error-details"],
                testValue = testVal,
                currentValue = currentVal,
                itgLink = $"{itgBaseURI}/{orgID}/assets/records/{lastRunAsset.id}",
                possibleServers = lastRunAsset.attributes.traits["devices-running-autodoc"]
            };

            return warning;
        }

        /// <summary>
        /// Builds the HTML response to return from the function which will display a webpage with all the warnings.
        /// </summary>
        /// <param name="warnings">A List of all the warnings for issues found. These warnings should each be created with the BuildWarning() function.</param>
        /// <returns>A formatted string with the response HTML</returns>
        private static string BuildResponseHTML(List<Warning> warnings)
        {
            if (warnings.Count < 1)
            {
                return @$"
                <html>
                    <head>
                        <title>No Issues Found</title>
                        <style>
                            {Constants.tableStyling}
                        </style>
                    </head>

                    <body>
                        <h1>No Issues Found! :D</h1>
                    </body>
                </html>";
            }

            string[] warningOrgs = warnings.Select(x => x.orgID).Distinct().ToArray<string>();
            string responseHTML = @$"
                <html>
                    <head>
                        <title>{warnings.Count} Issues Found!</title>
                        <style>
                            {Constants.tableStyling}
                        </style>
                    </head>

                    <body>";

            responseHTML += BuildResponseHTMLInner(warnings);

            responseHTML += @"        
                    </body>
                </html>
                ";

            return responseHTML;
        }

        /// <summary>
        /// A sub-function that builds the HTML for the individual warning tables.
        /// This is used by the BuildResponseHTML function for building a webpage, but also by email response directly as we don't need a full HTML page for email.
        /// </summary>
        /// <param name="warnings">A list of all the warnings for issues found.</param>
        /// <returns>A formatted string with the HTML of the warning tables</returns>
        private static string BuildResponseHTMLInner(List<Warning> warnings)
        {
            if (warnings.Count < 1)
            {
                return "";
            }

            string[] warningOrgs = warnings.Select(x => x.orgID).Distinct().ToArray<string>();
            string responseHTML = "";

            foreach (string orgID in warningOrgs)
            {
                Warning[] orgsWarnings = warnings.Where(x => x.orgID == orgID).Select(x => x).ToArray<Warning>();
                string orgName = orgsWarnings.FirstOrDefault().orgName;
                string itgLink = orgsWarnings.FirstOrDefault().itgLink;
                string orgsWarningTable = $"<a href='{itgLink}' class='title' target='_blank' rel='noopener noreferrer'><h2>{orgName}</h2></a>\n";

                foreach (Warning warning in orgsWarnings)
                {
                    string testValType = warning.error.Contains("Version") ? "Expected Version" : "Threshold in Days";
                    orgsWarningTable += @$"
                        <table class='styled-table'>
                            <thead>
                                <tr>
                                    <th align='left'>Error</th>
                                    <th align='left'>Current Value</th>
                                    <th align='left'>Test Value ({testValType})</th>
                                </tr>
                            </thead>
                            <tbody>
                                <tr class='highlight-row'>
                                    <td>{warning.error}</td>
                                    <td>{warning.currentValue}</td>
                                    <td>{warning.testValue}</td>
                                </tr>
                                <tr>
                                    <td colspan='3'>Details: {warning.errorDetails}</td>
                                </tr>
                                <tr>
                                    <td colspan='3'>Possible Servers: {warning.possibleServers}</td>
                                </tr>
                            </tbody>
                        </table>";
                    orgsWarningTable += "\n";
                }

                responseHTML += orgsWarningTable;
            }

            return responseHTML;
        }

        /// <summary>
        /// A template class for formatting the JSON returned from the ITGlue API.
        /// The structure is the high-level structure of the API response.
        /// </summary>
        private class FlexibleAssetAPIResponse
        {
            public FlexibleAssetData[] data { get; set; }
            public FlexibleAssetData[] included { get; set; }  
            public FlexibleAssetMetaDetails meta { get; set; }
        }

        /// <summary>
        /// A template class for formatting each object in the data array of the ITGlue JSON response.
        /// The structure is for each object in the data array.
        /// </summary>
        private class FlexibleAssetData : DynamicObject
        {
            public string id {get; set; }
            public string type {get; set; }
            public dynamic attributes {get; set; }
            public dynamic relationships {get; set; }
        }

        /// <summary>
        /// A template class for formatting the meta object of the ITGlue JSON response.
        /// The structure is for the meta object.
        /// </summary>
        private class FlexibleAssetMetaDetails
        {
            [JsonProperty(PropertyName="current-page")]
            public int current_page { get; set; }
            [JsonProperty(PropertyName="next-page")]
            public int? next_page { get; set; }
            [JsonProperty(PropertyName="prev-page")]
            public int? prev_page { get; set; }
            [JsonProperty(PropertyName="total-pages")]
            public int total_pages { get; set; }
            [JsonProperty(PropertyName="total-count")]
            public int total_count { get; set; }
            public object filters { get; set; }
        }

        /// <summary>
        /// A template class that contains the structure of a Warning object.
        /// The BuildWarning() class is used to create warnings.
        /// </summary>
        private class Warning
        {
            public string orgID { get; set; }
            public string orgName { get; set; }
            public string error { get; set; }
            public string errorDetails { get; set; }
            public string testValue { get; set; }
            public string currentValue { get; set; }
            public string itgLink { get; set; }
            public string possibleServers { get; set; }
        }
    }
}
