using LeadScoringWebApp.Models;
using Microsoft.PowerBI.Api.Beta;
using Microsoft.PowerBI.Security;
using Microsoft.Rest;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;

namespace LeadScoringWebApp.Controllers
{
    public class HomeController : Controller
    {
        private string _workspaceCollection;
        private string _workspaceId;
        private string _signingKey;
        private string _apiUrl;

        public HomeController()
        {
            _workspaceCollection = ConfigurationManager.AppSettings["powerbi:WorkspaceCollection"];
            _workspaceId = new Guid(ConfigurationManager.AppSettings["powerbi:WorkspaceId"]).ToString();
            _signingKey = ConfigurationManager.AppSettings["powerbi:SigningKey"];
            _apiUrl = ConfigurationManager.AppSettings["powerbi:ApiUrl"];
        }

        private IPowerBIClient CreatePowerBIClient(PowerBIToken token)
        {
            var jwt = token.Generate(_signingKey);
            var credentials = new TokenCredentials(jwt, "AppToken");
            var client = new PowerBIClient(credentials)
            {
                BaseUri = new Uri(_apiUrl)
            };

            return client;
        }

        public ActionResult ReportsSync()
        {
            var devToken = PowerBIToken.CreateDevToken(_workspaceCollection, _workspaceId);

            using (var client = this.CreatePowerBIClient(devToken))
            {
                var reportsResponse = client.Reports.GetReportsAsync(_workspaceCollection, _workspaceId.ToString());
                var report = reportsResponse.Result.Value.Last<Microsoft.PowerBI.Api.IReport>(r => r.Name == ConfigurationManager.AppSettings["powerbi:ReportName"]);
                report.EmbedUrl = report.EmbedUrl + "&filterPaneEnabled=false";
                var embedToken = PowerBIToken.CreateReportEmbedToken(_workspaceCollection, _workspaceId, report.Id);

                var viewModel = new ReportViewModel
                {
                    Report = report,
                    AccessToken = embedToken.Generate(_signingKey)
                };

                return PartialView(viewModel);
            }
        }

        public ActionResult Index()
        {
            return View();
        }

        public ActionResult Report()
        {
            ViewBag.Message = "Report page";
            ViewBag.AnotherMessage = "HelloWorld";

            return View();
        }
        
        public ActionResult Settings()
        {
            return View("~/Views/Home/Settings.cshtml");
        }

        private string GetStatusString(ApiController.JobId jobId)
        {
            switch (ApiController.jobStatuses[jobId])
            {
                case ApiController.JobStatus.NotStarted:
                    return "Not started";
                case ApiController.JobStatus.UploadingFile:
                    return "Uploading file";
                case ApiController.JobStatus.SubmittingJob:
                    return "Submitting job";
                case ApiController.JobStatus.Running:
                    return "Running";
                 case ApiController.JobStatus.Finished:
                    return "Job Finished!";
                case ApiController.JobStatus.Failed:
                    return "Job Failed :(";

                default:
                    return "Status not available";
            }
        }
        public string GetScoringStatus()
        {
            return GetStatusString(ApiController.JobId.Scoring);
        }
        public string GetLeadGenerationStatus()
        {
            return GetStatusString(ApiController.JobId.LeadGeneration);
        }

        public string GetRetrainingStatus()
        {
            var jobId = ApiController.JobId.Retraining;
            if (ApiController.jobStatuses[jobId] == ApiController.JobStatus.Running)
            {
                string queryString = "SELECT * FROM Status";
                string connectionString = "Server=mlleadscoring-test.database.windows.net,1433;Database=leadscoring-testV12;User Id=leadadmin;Password=Leadadm2712!;";

                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    SqlCommand command = new SqlCommand(queryString, connection);
                    command.Parameters.AddWithValue("@tPatSName", "Your-Parm-Value");
                    connection.Open();
                    SqlDataReader reader = command.ExecuteReader();
                    try
                    {
                        string status = "Cannot retrieve status";
                        while (reader.Read())
                        {
                            status = reader["Status"].ToString();
                        }
                        return status;
                    }
                    finally
                    {
                        // Always call Close when done reading.
                        reader.Close();
                    }
                }
            }
            else
            {
                return GetStatusString(jobId);
            }
        }

    }
}