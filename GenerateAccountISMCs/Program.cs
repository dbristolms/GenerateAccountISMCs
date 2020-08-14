using Microsoft.Azure.Management.Media;
using Microsoft.Azure.Management.Media.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Microsoft.Rest;
using Microsoft.Rest.Azure;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace GenerateAccountISMCs
{
    class Program
    {
        private static IAzureMediaServicesClient client = null;
        private static StreamingEndpoint streamingEndpoint = null;
        static void Main(string[] args)
        {
            ConfigWrapper config = new ConfigWrapper(new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .Build());
            try
            {
                RunApplication(config);
            }
            catch (Exception exception)
            {
                if (exception.Source.Contains("ActiveDirectory"))
                {
                    Console.Error.WriteLine("TIP: Make sure that you have filled out the appsettings.json file before running this sample.");
                }

                Console.Error.WriteLine($"{exception.Message}");

                ApiErrorException apiException = exception.GetBaseException() as ApiErrorException;
                if (apiException != null)
                {
                    Console.Error.WriteLine(
                        $"ERROR: API call failed with error code '{apiException.Body.Error.Code}' and message '{apiException.Body.Error.Message}'.");
                }
            }
            Console.WriteLine("Press Enter to continue.");
            Console.ReadLine();
        }

        private static void RunApplication(ConfigWrapper config)
        {
            client = CreateMediaServicesClient(config);
            // Set the polling interval for long running operations to 2 seconds.
            // The default value is 30 seconds for the .NET client SDK
            client.LongRunningOperationRetryTimeout = 2;

            // Connect to Storage.
            CloudStorageAccount storageAccount = CloudStorageAccount.Parse(config.StorageConnectionString);
            CloudBlobClient cloudBlobClient = storageAccount.CreateCloudBlobClient();

            StartEndpointIfNotRunning(config);

            // Get a list of all of the locators and enumerate through them a page at a time.
            IPage<StreamingLocator> firstPage = client.StreamingLocators.List(config.ResourceGroup, config.AccountName);
            IPage<StreamingLocator> currentPage = firstPage;

            do
            {
                bool always = false;
                foreach (StreamingLocator locator in currentPage)
                {
                    // Get the asset associated with the locator.
                    Asset asset = client.Assets.Get(config.ResourceGroup, config.AccountName, locator.AssetName);

                    // Get the Storage continer associated with the asset.
                    CloudBlobContainer storageContainer = cloudBlobClient.GetContainerReference(asset.Container);

                    // Get a manifest file list from the Storage container.
                    List<string> fileList = GetFilesListFromStorage(storageContainer);

                    string ismcFileName = fileList.Where(a => a.ToLower().Contains(".ismc")).FirstOrDefault();
                    string ismManifestFileName = fileList.Where(a => a.ToLower().EndsWith(".ism")).FirstOrDefault();
                    // If there is no .ism then there's no reason to continue.  If there's no .ismc we need to add it.
                    if (ismManifestFileName != null && ismcFileName == null)
                    {
                        Console.WriteLine("Asset {0} does not have an ISMC file.", asset.Name);
                        if (!always)
                        {
                            Console.WriteLine("Add the ISMC?  (y)es, (n)o, (a)lways, (q)uit");
                            ConsoleKeyInfo response = Console.ReadKey();
                            string responseChar = response.Key.ToString();

                            if (responseChar.Equals("N"))
                                continue;
                            if (responseChar.Equals("A"))
                            {
                                always = true;
                            }
                            else if (!(responseChar.Equals("Y")))
                            {
                                break; // At this point anything other than a 'yes' should quit the loop/application.
                            }
                        }

                        string streamingUrl = GetStreamingUrlsAndDrmType(client, config.ResourceGroup, config.AccountName, locator.Name);
                        // We should only have two items in the list.  First is the Smooth Streaming URL, and second is the DRM scheme
                        if(streamingUrl.Length == 0)
                        {
                            // error state, skip this asset.  We shouldn't ever be here.
                            continue;
                        }

                        string ismcContentXml = SendManifestRequest(new Uri(streamingUrl));
                        if(ismcContentXml.Length == 0)
                        {
                            //error state, skip this asset
                            continue;
                        }
                        if(ismcContentXml.IndexOf("<Protection>") > 0)
                        {
                            Console.WriteLine("Content is encrypted. Removing the protection header from the client manifest.");
                            //remove DRM from the ISCM manifest
                            ismcContentXml = Xml.RemoveXmlNode(ismcContentXml);
                        }
                        string newIsmcFileName = ismManifestFileName.Substring(0, ismManifestFileName.IndexOf(".")) + ".ismc";
                        CloudBlockBlob ismcBlob = WriteStringToBlob(ismcContentXml, newIsmcFileName, storageContainer);

                        // Download the ISM so that we can modify it to include the ISMC file link.
                        string ismXmlContent = GetFileXmlFromStorage(storageContainer, ismManifestFileName);
                        ismXmlContent = Xml.AddIsmcToIsm(ismXmlContent, newIsmcFileName);
                        WriteStringToBlob(ismXmlContent, ismManifestFileName, storageContainer);
                        // update the ism to point to the ismc (download, modify, delete original, upload new)
                    }
                }
                // Continue on to the next page of locators.
                try
                {
                    currentPage = client.StreamingLocators.ListNext(currentPage.NextPageLink);
                }
                catch (Exception)
                {
                    // we'll get here at the end of the page when the page is empty.  This is okay.
                }
            } while (currentPage.NextPageLink != null);
        }        

        private static string GetFileXmlFromStorage(CloudBlobContainer storageContainer, string ismManifestFileName)
        {
            CloudBlockBlob blob = storageContainer.GetBlockBlobReference(ismManifestFileName);
            return blob.DownloadText();
        }

        private static void StartEndpointIfNotRunning(ConfigWrapper config)
        {
            const string DefaultStreamingEndpointName = "default";
            streamingEndpoint = client.StreamingEndpoints.Get(config.ResourceGroup, config.AccountName, DefaultStreamingEndpointName);

            if (streamingEndpoint != null)
            {
                if (streamingEndpoint.ResourceState != StreamingEndpointResourceState.Running)
                {
                    client.StreamingEndpoints.StartAsync(config.ResourceGroup, config.AccountName, DefaultStreamingEndpointName);
                }
            }
        }

        private static CloudBlockBlob WriteStringToBlob(string ContentXml, string fileName, CloudBlobContainer storageContainer)
        {
            CloudBlockBlob newBlob = storageContainer.GetBlockBlobReference(fileName);
            newBlob.UploadText(ContentXml);
            return newBlob;
        }

        private static string SendManifestRequest(Uri url)
        {
            string response = string.Empty;
            if (url.IsWellFormedOriginalString())
            {
                HttpWebRequest myHttpWebRequest = null;
                HttpWebResponse myHttpWebResponse = null;
                try
                {
                    // Creates an HttpWebRequest with the specified URL. 
                    myHttpWebRequest = (HttpWebRequest)WebRequest.Create(url);
                    // Sends the HttpWebRequest and waits for the response.			
                    myHttpWebResponse = (HttpWebResponse)myHttpWebRequest.GetResponse();

                    if (myHttpWebResponse.StatusCode == HttpStatusCode.OK)
                    {
                        using (Stream responseStream = myHttpWebResponse.GetResponseStream())
                        {
                            using (StreamReader reader = new StreamReader(responseStream))
                                response = reader.ReadToEnd();
                        }
                    }
                    myHttpWebResponse.Close();
                }
                catch (Exception e)
                {
                    //Console.WriteLine("Error: " + e.Message);
                }
            }
            return response;
        }

        private static List<string> GetFilesListFromStorage(CloudBlobContainer storageContainer)
        {
            List<CloudBlockBlob> fullBlobList = storageContainer.ListBlobs().OfType<CloudBlockBlob>().ToList();
            // Filter the list to only contain .ism and .ismc files
            IEnumerable<string> filteredList = from b in fullBlobList
                       where b.Name.ToLower().Contains(".ism")
                       select b.Name;
            return filteredList.ToList();
        }

        private static string GetStreamingUrlsAndDrmType(IAzureMediaServicesClient client, string resourceGroupName, string accountName, String locatorName)
        {
            string streamingUrl = string.Empty;

            ListPathsResponse paths = client.StreamingLocators.ListPaths(resourceGroupName, accountName, locatorName);

            foreach (StreamingPath path in paths.StreamingPaths)
            {
                if (path.StreamingProtocol == StreamingPolicyStreamingProtocol.SmoothStreaming)
                {
                    UriBuilder uriBuilder = new UriBuilder();
                    uriBuilder.Scheme = "https";
                    uriBuilder.Host = streamingEndpoint.HostName;
                    uriBuilder.Path = path.Paths[0];
                    streamingUrl = uriBuilder.ToString();
                }
            }
            return streamingUrl;
        }

        private static IAzureMediaServicesClient CreateMediaServicesClient(ConfigWrapper config)
        {
            ArmClientCredentials credentials = new ArmClientCredentials(config);

            return new AzureMediaServicesClient(config.ArmEndpoint, credentials)
            {
                SubscriptionId = config.SubscriptionId,
            };
        }
    }
    public class ArmClientCredentials : ServiceClientCredentials
    {

        private readonly AuthenticationContext _authenticationContext;
        private readonly Uri _customerArmAadAudience;
        private readonly ClientCredential _clientCredential;
        public ArmClientCredentials(ConfigWrapper config)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            var authority = config.AadEndpoint.AbsoluteUri + config.AadTenantId;

            _authenticationContext = new AuthenticationContext(authority);
            _customerArmAadAudience = config.ArmAadAudience;
            _clientCredential = new ClientCredential(config.AadClientId, config.AadSecret);
        }
        public async override Task ProcessHttpRequestAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var token = await _authenticationContext.AcquireTokenAsync(_customerArmAadAudience.OriginalString, _clientCredential);
            request.Headers.Authorization = new AuthenticationHeaderValue(token.AccessTokenType, token.AccessToken);
            await base.ProcessHttpRequestAsync(request, cancellationToken);
        }
    }
}