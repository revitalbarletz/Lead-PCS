using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;
using Microsoft.Ajax.Utilities;
using Newtonsoft.Json;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http;
using System.IO;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.Auth;
using Microsoft.PowerBI.Security;
using Microsoft.PowerBI.Api.Beta;
using Microsoft.Rest;
using LeadScoringWebApp.Models;
using System.Configuration;
using System.Data.SqlClient;

namespace LeadScoringWebApp.Controllers
{
    public class ApiController : Controller
    {
        public class AzureBlobDataReference
        {
            // Storage connection string used for regular blobs. It has the following format:
            // DefaultEndpointsProtocol=https;AccountName=ACCOUNT_NAME;AccountKey=ACCOUNT_KEY
            // It's not used for shared access signature blobs.
            public string ConnectionString { get; set; }

            // Relative uri for the blob, used for regular blobs as well as shared access 
            // signature blobs.
            public string RelativeLocation { get; set; }

            // Base url, only used for shared access signature blobs.
            public string BaseLocation { get; set; }

            // Shared access signature, only used for shared access signature blobs.
            public string SasBlobToken { get; set; }
        }

        public enum BatchScoreStatusCode
        {
            NotStarted,
            Running,
            Failed,
            Cancelled,
            Finished
        }

        public class BatchScoreStatus
        {
            // Status code for the batch scoring job
            public BatchScoreStatusCode StatusCode { get; set; }

            // Locations for the potential multiple batch scoring outputs
            public IDictionary<string, AzureBlobDataReference> Results { get; set; }

            // Error details, if any
            public string Details { get; set; }
        }

        public class BatchExecutionRequest
        {
            public IDictionary<string, AzureBlobDataReference> Inputs { get; set; }
            public IDictionary<string, string> GlobalParameters { get; set; }

            // Locations for the potential multiple batch scoring outputs
            public IDictionary<string, AzureBlobDataReference> Outputs { get; set; }
        }

        public enum JobId
        {
            Scoring,
            Retraining,
            LeadGeneration
        };
        public enum JobStatus
        {
            NotStarted,
            UploadingFile,
            SubmittingJob,
            Running,
            Finished,
            Failed
        };

        public static Dictionary<JobId, JobStatus> jobStatuses = new Dictionary<JobId, JobStatus>()
        {
            { JobId.Scoring, JobStatus.NotStarted },
            { JobId.Retraining, JobStatus.NotStarted },
            { JobId.LeadGeneration, JobStatus.NotStarted }
        };

        [HttpPost]
        public async Task<ActionResult> Score(ICollection<HttpPostedFileBase> inputFiles)
        {
            string StorageAccountName = ConfigurationManager.AppSettings["StorageAccountName"];
            string StorageAccountKey = ConfigurationManager.AppSettings["StorageAccountKey"];
            string StorageContainerName = ConfigurationManager.AppSettings["StorageContainerName"];
            
            string storageConnectionString = string.Format("DefaultEndpointsProtocol=https;AccountName={0};AccountKey={1}", StorageAccountName, StorageAccountKey);

            jobStatuses[JobId.Scoring] = JobStatus.UploadingFile;

            UploadFileToBlob(
               inputFiles.ElementAt(0),
               "CustomerDatasetForScoring.tsv",
               StorageContainerName, storageConnectionString);

            CleanupDB(new List<string> { "ScoredLeads" });

            await InvokeBatchExecutionService(inputFiles.ElementAt(0),
                ConfigurationManager.AppSettings["ScoringApiUrl"],
                ConfigurationManager.AppSettings["ScoringApiKey"], 
                "CustomerDatasetForScoring.tsv",
                "input1",
                JobId.Scoring);

            //This pulls back in the model and generated leads (which are only set on model retrain or reset DB)
            // the scenario here is if you do a scoring after a DB clean, you'll not ahve any data for model and generated leads
            ForceUpdateModelAndGeneratedLeads();

            return View("~/Views/Home/index.cshtml");
        }

        [HttpPost]
        public async Task<ActionResult> Retrain(ICollection<HttpPostedFileBase> inputFiles)
        {
            string StorageAccountName = ConfigurationManager.AppSettings["StorageAccountName"];
            string StorageAccountKey = ConfigurationManager.AppSettings["StorageAccountKey"];
            string StorageContainerName = ConfigurationManager.AppSettings["StorageContainerName"];
            
            string storageConnectionString = string.Format("DefaultEndpointsProtocol=https;AccountName={0};AccountKey={1}", StorageAccountName, StorageAccountKey);

            jobStatuses[JobId.Retraining] = JobStatus.UploadingFile;
            jobStatuses[JobId.LeadGeneration] = JobStatus.UploadingFile;

            UploadFileToBlob(
               inputFiles.ElementAt(0),
               "CustomerDatasetForRetraining.tsv",
               StorageContainerName, storageConnectionString);

            CleanupDB(new List<string> { "GeneratedLeads", "ModelEvaluation" });

            var task1 = RetrainService(inputFiles.ElementAt(0));
            var task2 = GenerateLeads(inputFiles.ElementAt(0));

            await Task.WhenAll(task1, task2);

            return View("~/Views/Home/Settings.cshtml");
        }

        [HttpPost]
        public ActionResult ResetDB()
        {
            using (SqlConnection sqlConnection = new SqlConnection(ConfigurationManager.AppSettings["SqlConnectionString"]))
            {
                sqlConnection.Open();
                SqlCommand cmd = new SqlCommand();
                SqlDataReader reader;

                cmd.CommandText = "TRUNCATE TABLE GeneratedLeads; TRUNCATE TABLE ScoredLeads; TRUNCATE TABLE ModelEvaluation; INSERT INTO GeneratedLeads SELECT WebDomain, ScoredLabels, ScoredProbabilities FROM DemoGeneratedLeads; INSERT INTO ScoredLeads SELECT WebDomain, AccountNameNorm, RawCountry, RawCity, ScoredLabels, ScoredProbabilities FROM DemoScoredLeads; INSERT INTO ModelEvaluation SELECT Accuracy, Precision, Recall, FScore, AUC, AverageLogLoss, TrainingLogLoss FROM DemoModelEvaluation;";
                cmd.CommandType = CommandType.Text;
                cmd.Connection = sqlConnection;

                reader = cmd.ExecuteReader();
            }

            //Reset the job status on resetDB to Job Finsished
            jobStatuses[ApiController.JobId.Scoring] = JobStatus.Finished;

            return View("~/Views/Home/Settings.cshtml");
        }

        [HttpPost]
        public ActionResult CleanDB()
        {
            using (SqlConnection sqlConnection = new SqlConnection(ConfigurationManager.AppSettings["SqlConnectionString"]))
            {
                sqlConnection.Open();
                SqlCommand cmd = new SqlCommand();
                SqlDataReader reader;

                cmd.CommandText = "TRUNCATE TABLE GeneratedLeads; TRUNCATE TABLE ScoredLeads; TRUNCATE TABLE ModelEvaluation";
                cmd.CommandType = CommandType.Text;
                cmd.Connection = sqlConnection;

                reader = cmd.ExecuteReader();
            }

            //Reset the job status on cleanDB
            jobStatuses[ApiController.JobId.Scoring] = JobStatus.NotStarted;

            return View("~/Views/Home/Settings.cshtml");
        }

        private void CleanupDB(List<string> tables)
        {
            using (SqlConnection sqlConnection = new SqlConnection(ConfigurationManager.AppSettings["SqlConnectionString"]))
            {
                sqlConnection.Open();
                SqlCommand cmd = new SqlCommand();
                SqlDataReader reader;
                StringBuilder tablesString = new StringBuilder();
                foreach (var table in tables)
                {
                    tablesString.Append("TRUNCATE TABLE " + table + ";");
                }
                cmd.CommandText = tablesString.ToString();
                cmd.CommandType = CommandType.Text;
                cmd.Connection = sqlConnection;

                reader = cmd.ExecuteReader();
            }
        }


        public void ForceUpdateModelAndGeneratedLeads()
        {
            using (SqlConnection sqlConnection = new SqlConnection(ConfigurationManager.AppSettings["SqlConnectionString"]))
            {
                sqlConnection.Open();
                SqlCommand cmd = new SqlCommand();
                SqlDataReader reader;

                cmd.CommandText = "TRUNCATE TABLE GeneratedLeads; TRUNCATE TABLE ModelEvaluation; INSERT INTO GeneratedLeads SELECT WebDomain, ScoredLabels, ScoredProbabilities FROM DemoGeneratedLeads; INSERT INTO ModelEvaluation SELECT Accuracy, Precision, Recall, FScore, AUC, AverageLogLoss, TrainingLogLoss FROM DemoModelEvaluation;";
                cmd.CommandType = CommandType.Text;
                cmd.Connection = sqlConnection;

                reader = cmd.ExecuteReader();
            }
        }

        static async Task WriteFailedResponse(HttpResponseMessage response)
        {
            Console.WriteLine(string.Format("The request failed with status code: {0}", response.StatusCode));

            // Print the headers - they include the requert ID and the timestamp, which are useful for debugging the failure
            Console.WriteLine(response.Headers.ToString());

            string responseContent = await response.Content.ReadAsStringAsync();
            Console.WriteLine(responseContent);
        }

        static void UploadFileToBlob(HttpPostedFileBase file, string inputBlobName, string storageContainerName, string storageConnectionString)
        {
            Console.WriteLine("Uploading the input to blob storage...");

            var blobClient = CloudStorageAccount.Parse(storageConnectionString).CreateCloudBlobClient();
            var container = blobClient.GetContainerReference(storageContainerName);
            container.CreateIfNotExists();
            var blob = container.GetBlockBlobReference(inputBlobName);
            using (MemoryStream target = new MemoryStream())
            {
                file.InputStream.CopyTo(target);
                byte[] data = target.ToArray();
                blob.UploadFromByteArray(data, 0, data.Length);
            };
        }

        static async Task RetrainService(HttpPostedFileBase file)
        {
            await InvokeBatchExecutionService(file,
                ConfigurationManager.AppSettings["RetrainingApiUrl"],
                ConfigurationManager.AppSettings["RetrainingApiKey"],
                "CustomerDatasetForRetraining.tsv",
                "Customer dataset input",
                JobId.Retraining);
        }

        static async Task GenerateLeads(HttpPostedFileBase file)
        {
            await InvokeBatchExecutionService(file,
                ConfigurationManager.AppSettings["LeadsGenerationApiUrl"],
                ConfigurationManager.AppSettings["LeadsGenerationApiKey"],
                "CustomerDatasetForRetraining.tsv",
                "Customer dataset input",
                JobId.LeadGeneration);

        }

        static async Task InvokeBatchExecutionService(HttpPostedFileBase file, string baseUrl, string apiKey, string blobFileName, string inputName, JobId jobId)
        {
            string StorageAccountName = ConfigurationManager.AppSettings["StorageAccountName"];
            string StorageAccountKey = ConfigurationManager.AppSettings["StorageAccountKey"];
            string StorageContainerName = ConfigurationManager.AppSettings["StorageContainerName"];

            // set a time out for polling status
            const int TimeOutInMilliseconds = 3600 * 1000; // Set a timeout of 2 minutes

            string storageConnectionString = string.Format("DefaultEndpointsProtocol=https;AccountName={0};AccountKey={1}", StorageAccountName, StorageAccountKey);

            using (HttpClient client = new HttpClient())
            {
                var request = new BatchExecutionRequest()
                {
                    Inputs = new Dictionary<string, AzureBlobDataReference>()
                    {
                        {
                            inputName,
                            new AzureBlobDataReference()
                            {
                                ConnectionString = storageConnectionString,
                                RelativeLocation = string.Format("{0}/"+blobFileName, StorageContainerName)
                            }
                        },
                    },
                    GlobalParameters = new Dictionary<string, string>()
                    {
                    }
                };

                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                // submit the job
                jobStatuses[jobId] = JobStatus.SubmittingJob;
                var response = await client.PostAsJsonAsync(baseUrl + "?api-version=2.0", request);
                if (!response.IsSuccessStatusCode)
                {
                    jobStatuses[jobId] = JobStatus.Failed;
                    await WriteFailedResponse(response);
                    return;
                }

                string jobGuid = await response.Content.ReadAsAsync<string>();

                // start the job
                jobStatuses[jobId] = JobStatus.Running;
                response = await client.PostAsync(baseUrl + "/" + jobGuid + "/start?api-version=2.0", null);
                if (!response.IsSuccessStatusCode)
                {
                    jobStatuses[jobId] = JobStatus.Failed;
                    await WriteFailedResponse(response);
                    return;
                }

                string jobLocation = baseUrl + "/" + jobGuid + "?api-version=2.0";
                Stopwatch watch = Stopwatch.StartNew();
                bool done = false;
                while (!done)
                {
                    //ApiController.statusString = "Checking the job status...";
                    response = await client.GetAsync(jobLocation);
                    if (!response.IsSuccessStatusCode)
                    {
                        await WriteFailedResponse(response);
                        return;
                    }

                    BatchScoreStatus status = await response.Content.ReadAsAsync<BatchScoreStatus>();
                    if (watch.ElapsedMilliseconds > TimeOutInMilliseconds)
                    {
                        done = true;
                        //ApiController.statusString = string.Format("Timed out. Deleting job {0} ...", jobGuid);
                        jobStatuses[jobId] = JobStatus.Failed;
                        await client.DeleteAsync(jobLocation);
                    }
                    switch (status.StatusCode)
                    {
                        case BatchScoreStatusCode.NotStarted:
                            Console.WriteLine(string.Format("Job {0} not yet started...", jobGuid));
                            break;
                        case BatchScoreStatusCode.Running:
                            Console.WriteLine(string.Format("Job {0} running...", jobGuid));
                            break;
                        case BatchScoreStatusCode.Failed:
                            Console.WriteLine(string.Format("Job {0} failed!", jobGuid));
                            Console.WriteLine(string.Format("Error details: {0}", status.Details));
                            jobStatuses[jobId] = JobStatus.Failed;
                            done = true;
                            break;
                        case BatchScoreStatusCode.Cancelled:
                            Console.WriteLine(string.Format("Job {0} cancelled!", jobGuid));
                            jobStatuses[jobId] = JobStatus.Failed;
                            done = true;
                            break;
                        case BatchScoreStatusCode.Finished:
                            done = true;
                            Console.WriteLine(string.Format("Job {0} finished!", jobGuid));
                            jobStatuses[jobId] = JobStatus.Finished;

                            //ProcessResults(status);
                            break;
                    }

                    if (!done)
                    {
                        Thread.Sleep(1000); // Wait one second
                    }
                }

                //ApiController.statusString = "Ellapsed time: " + watch.ElapsedMilliseconds;
            }
        }
        
    }
}
