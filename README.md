# MonitoringAgent
This Azure Function is used to monitor other scripts and ensure they are running correctly. For this to work there are a few prerequisites:
1. A flexible asset must exist in ITGlue that contains information on when scripts last ran. Our asset is named "Scripts - Last Run".
2. All scripts that you want to monitor must update the "last run" asset in ITGlue under their respective company.
3. This script can then be setup and it will query the "last run" assets to monitor how long it has been since each script last ran.
This script cannot pick up on other underlying issues in scripts, but if a script cannot run for whatever reason, it will be unable to update the "last run" asset and then this monitoring agent will recognize a problem. 

The script is meant to be deployed to Azure as a function via VS Code and the Azure extension. 

**To setup**, create a `local.settings.json` file from the template and add your SendGrid & ITGlue api keys to the local settings environment variables. It also requires the ITGlue base url (for opening links), the ITGlue API url, and the ID of the "Last Run" flexible asset. Additionally, you can enter email details to allow the agent to send email alerts using SendGrid. `Email_From_Email` and `Email_From_Name` are where the email is being sent from, this email domain must be whitelisted in SendGrid for it to work. `Emails_To` is a comma-separated list of email addresses you'd like the alerts to go out to. 

You must also create a `Constants.cs` file from the template and can modify the variables within it to change what is monitored and to modify templates:
- `lastRunChecks` - These are the primary tests to run on each "last run" asset. You can create a new object to add new tests or delete tests you do not want. The following breaks down how the objects are formed:
    * The key at the top of each object should match the field name in the "last run" asset e.g. `current-version`. This is the field you are checking against.
    * You can either set the `latest-version` key to a version number to verify a script is up-to-date, or set the `inactive-days` key to set a threshold for how often a script should run.
        - If setting `latest-version`, the version # must be equal to the test version or a warning will be thrown. You can also set `version-updated-on` to a date and it will only check the version number if the script has ran at least once after that date. This is useful if a script auto-updates when it runs, the version number may have been updated but the script will only update when it next runs, no point testing before then.
        - If setting `inactive-days`, the script will query how long it has been since the script last ran, and if it has been more than X days, it will trigger a warning. 
    * `error` - A label for the error.
    * `error-details` - Further details on the error.
- `customLastRunChecks` - These are tests for Custom Scripts. These check the "Custom Scripts" field on the "last run" asset (a textbox) which can contain multiple custom scripts and the date when they last ran. Entries in the Custom Scripts textbox should be in the format "Custom Script: dd/mm/yyyy" and each entry should be on a separate line. The monitoring agent will then check each line against these tests and get the date when they last ran for the audit. These tests are similar to those in `lastRunChecks`, but each set of tests is nested under a Dictionary key that is the OrgID of the company in ITGlue. Set the OrgID and then nest any applicable tests under the organization.
- `tableStyling` - This is the CSS styling of the returned web page when using the HTML return type.
- `emailTemplate` - This is the HTML template for emails.

Push all of this to an Azure function and then you will be able to use the Monitoring Agent to watch for issues in your automated scripts. 

**To use the Monitoring Agent**, you can query it using the functions url and the GET parameter `returnType`. There are 3 options for `returnType`:
- `json` - returns all of the warnings in JSON format (default)
- `html` - returns a webpage with a table of all the warnings
- `email` - sends an email using SendGrid with a list of all the warnings

**To schedule periodic checks**, you can either use the MonitoringAgent-Scheduler Azure Function, or simply write a cron job (or use task scheduler) to query this function's URL with the GET parameter `?returnType=email` appended to it.