using System;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Text;
using System.Web;
using HtmlAgilityPack;
using System.Linq;
using System.Collections.Generic;
using System.Threading;

namespace WellDataScraper
{
    class Program
    {

        public static StringBuilder finalString;
        public static StringBuilder errorList;
        public static List<int> wellNumbers;

        public static string fileIn;
        public static string fileOut;
        public static int numMonths;
        static void Main(string[] args)
        {
            finalString = new StringBuilder();
            finalString.Append("API Number,Well Number,Month of Production,Year of Production,Days,BBLS Oil,BBLS Water,MCF Prod\n");
            errorList = new StringBuilder();
            errorList.Append("Well Number,Error\n");
            wellNumbers = new List<int>();

            getInputs();

            readFileNumbers();

            for (int currentWellNumber = 0; currentWellNumber < wellNumbers.Count(); currentWellNumber++)
            {
                Console.WriteLine("Getting data for well " + wellNumbers[currentWellNumber] + "...");
                Thread.Sleep(15000);    //15 seconds
                try
                {
                    //actually get the data and parse it
                    getPageData(wellNumbers[currentWellNumber]);

                }
                catch (WebException ex)
                {
                    //just try again
                    currentWellNumber--;
                }
                catch (ApplicationException ex)
                {
                    if (ex.Message == "Confidential")
                    {
                        Console.WriteLine(wellNumbers[currentWellNumber] + " is in confidential status. Skipping...");
                        errorList.Append(wellNumbers[currentWellNumber] + ",CONFIDENTIAL\n");
                    } else if (ex.Message == "No data found")
                    {
                        Console.WriteLine("No data found for " + wellNumbers[currentWellNumber] + ". Skipping...");
                        errorList.Append(wellNumbers[currentWellNumber] + ",INSUFFICIENT DATA\n");
                    }
                }
            }
            File.WriteAllText(fileOut + "\\" + DateTime.Now.ToShortDateString().Replace("/", "") + "_Output.csv" , finalString.ToString());
            File.WriteAllText(fileOut + "\\" + DateTime.Now.ToShortDateString().Replace("/", "") + "_Errors.csv", errorList.ToString());
            Console.WriteLine("Completed. Press enter to exit...");
            Console.ReadLine();
        }

        public static void getInputs()
        {
            Console.WriteLine("Path to input file:");
            fileIn = Console.ReadLine();
            while (!File.Exists(fileIn))
            {
                Console.WriteLine("Input file not found. Path to input file:");
                fileIn = Console.ReadLine();
            }
            Console.WriteLine("Path for output file (this should be a directory, not a specific file):");
            fileOut = Console.ReadLine();
            while (!Directory.Exists(fileOut))
            {
                Console.WriteLine("Invalid directory path. Path for output file:");
                fileOut = Console.ReadLine();
            }
            Console.WriteLine("Number of months to fetch per well:");
            numMonths = Int32.Parse(Console.ReadLine());
        }

        public static void readFileNumbers()
        {
            StreamReader file = new StreamReader(fileIn);
            string line;
            file.ReadLine(); //don't need that darn header
            while ((line = file.ReadLine()) != null)
            {
                //only grab what's after the comma
                string wellNumber = line.Substring(line.LastIndexOf(',') + 1);
                wellNumbers.Add(Int32.Parse(wellNumber));
            }

            file.Close();
        }

        public static void getPageData(int fileNumber)
        {
            WebRequest request = WebRequest.Create("https://www.dmr.nd.gov/oilgas/feeservices/getwellprod.asp");

            NameValueCollection outgoingQueryString = HttpUtility.ParseQueryString(String.Empty);
            outgoingQueryString.Add("FileNumber", fileNumber + "");
            string postData = outgoingQueryString.ToString();
 
            ASCIIEncoding ascii = new ASCIIEncoding();
            byte[] postBytes = ascii.GetBytes(postData.ToString());

            //other headers, don't mind the plaintext creds
            request.Credentials = new NetworkCredential("username", "password");
            request.Method = "POST";
            request.ContentType = "application/x-www-form-urlencoded";
            request.ContentLength = postBytes.Length;

            //actually post the data
            Stream postStream = request.GetRequestStream();
            postStream.Write(postBytes, 0, postBytes.Length);
            postStream.Close();

            //chill out and wait for a response and begin reading it
            WebResponse response = request.GetResponse();
            Stream data = response.GetResponseStream();

            StreamReader responseReader = new StreamReader(data);
            string responseString = responseReader.ReadToEnd();
            response.Close();

            //grab the whole thing and start parsing
            string tempString = extractData(responseString, fileNumber);

            finalString.Append(tempString);
             
        }

        private static string extractData(string responseString, int wellNumber)
        {
            string decodedString = HttpUtility.HtmlDecode(responseString);
            var HTML = new HtmlDocument();
            HTML.LoadHtml(decodedString);
            //if the status contains something about it being confidential, skip it
            if (HTML.DocumentNode.SelectNodes("//body/table")[1].ChildNodes[1].InnerHtml.Contains("ON CONFIDENTIAL STATUS"))
            {
                throw new ApplicationException("Confidential");
            }

            //otherwise if there's no table that holds the data then something is wrong
            if (HTML.DocumentNode.SelectNodes("//body/table")[1].ChildNodes[2].Element("table") == null) //the page decided to go back to the search, not cool
            {
                //at the bottom of the page when this happens there's a string saying so, so if we find that then there's no data so log that one
                if (HTML.DocumentNode.SelectNodes("//body/table")[1].ChildNodes[1].InnerHtml.Contains("No production data found for this well"))
                {
                    throw new ApplicationException("No data found");
                    
                }

                //otherwise, the page just didn't load and went back to the normal search so try again
                else
                {
                    throw new WebException("Page failed to load, trying again...");
                }  
            }
    
            //otherwise grab all of the tr elements containing the data and throw them in an array
            //note: yes, this is magic here - it is strictly based on the page layout staying exactly the same
            var htmlRows = HTML.DocumentNode.SelectNodes("//body/table")[1].ChildNodes[2].Element("table").ChildNodes.Where(node => node.OriginalName == "tr").Take(numMonths);
            var apiNumber = HTML.DocumentNode.SelectNodes("//body/table")[1].ChildNodes[1].ChildNodes[1].ChildNodes[1].Elements("b").ElementAt(1).InnerHtml;
            apiNumber = apiNumber.Replace("-", "");
            StringBuilder tempString = new StringBuilder();
          
            //actually pull the data out by doing a little bit more magic
            foreach (var node in htmlRows)
            {
                /**
                 * 0: Pool
                 * 1: Date
                 * 2: Days
                 * 3: BBLS Oil
                 * 4: Runs
                 * 5: BBLS Water
                 * 6: MCF Prod
                 * 7: MCF Sold
                 * 8: Vent/Flare
                 */
                var nodeChildren = node.ChildNodes;
                tempString.Append(apiNumber + ",");                //API Number
                tempString.Append(wellNumber + ",");                //Well Number (not in HTML)
                string date = nodeChildren[1].InnerHtml.ToString();
                if (date.Length == 6)   //the month is between 1 and 9
                {
                    date = "0" + date;
                }
                string month = date.Substring(0, date.IndexOf("-"));
                string year = date.Substring(date.IndexOf("-") + 1, date.Length - 3);
                tempString.Append(month + ",");                     //Month
                tempString.Append(year + ",");                      //Year
                tempString.Append(nodeChildren[2].InnerHtml + ","); //Days
                tempString.Append(nodeChildren[3].InnerHtml + ","); //BBLS Oil
                tempString.Append(nodeChildren[5].InnerHtml + ","); //BBLS Water
                tempString.Append(nodeChildren[6].InnerHtml + "\n"); //MCF Prod
            }

            return tempString.ToString();
        }
    }
}
