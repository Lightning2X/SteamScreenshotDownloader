// Uncomment this to only download the first 10 files (for debugging its easier to not download everything)
#define PARTIAL_DOWNLOAD

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SteamDownloader
{
    class Program
    {

        static List<ulong> FILES = new List<ulong>();
        static long STEAM_ID;
        static string DOWNLOAD_FOLDER => $"./Screenshots/{STEAM_ID}";
        static string USER_AGENT => "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/80.0.3987.132 Safari/537.36";
        static readonly Dictionary<string, string> MIME_TO_EXTENSION = new Dictionary<string, string>()
        {
            { "image/jpeg", ".jpg" },
            { "image/png", ".png" },
            { "image/gif", ".gif" },
            { "image/webp", ".webp" },
        };
        static int MAX_RETRIES => 3;
        static int RETRY_DELAY => 3000;
        static int TASK_LIMIT => 16;
        static string Version => "1.0";

        static async Task Main(string[] args)
        {
            Console.WriteLine($"Steam Screenshot Downloader V{Version}");
            Console.WriteLine();
            Console.WriteLine("Please fill in your Steam64 ID. You can find it by googling steam id finder and entering your URL there. Example Steam64 ID: 76561198053864545");
            Console.WriteLine("Please paste your Steam ID by right clicking or pressing ctrl V:");
            STEAM_ID = ReadSteamId();

            // Make sure the folder exists
            if (!Directory.Exists(DOWNLOAD_FOLDER))
                Directory.CreateDirectory(DOWNLOAD_FOLDER);

            // Scan all the user's pages and save the files in them to the FILES variable
            await ScanPages();

            // Remove any duplicates
            FILES = FILES.Distinct().ToList();

            if (FILES.Count == 0)
            {
                Console.WriteLine();
                Console.WriteLine("No screenshots found. Is the profile set to private?");
                return;
            }

            Console.WriteLine($"Found {FILES.Count()} Screenshots");

            // Download them all
            await DownloadImages();

            Console.WriteLine();
            Console.WriteLine($"All done! You can see the screenshots in \"/{Path.GetFullPath(DOWNLOAD_FOLDER)}/\"");
        }

        /// <summary>
        /// Reads a SteamId from console and returns it as a long
        /// </summary>
        /// <returns>The Steam ID</returns>
        static long ReadSteamId()
        {
            var read = Console.ReadLine().Trim();
            if (!long.TryParse(read, out long result))
            {
                Console.Write("That doesn't look like a number to me, please fill in a valid SteamID:");
                return ReadSteamId();
            }
            return result;
        }

        /// <summary>
        /// Scans the user's steam screenshot pages
        /// </summary>
        /// <returns>A task that performs the page scan</returns>
        static async Task ScanPages()
        {
            int page = 1;
            while (true)
            {
                Console.WriteLine($"Getting Page {page} ({FILES.Count} screenshots found)");

                int fails = 0;
                while (!await GetPage(page, STEAM_ID.ToString()))
                {
                    fails++;
                    Console.WriteLine($"Page {page} didn't have any screenshots, skipping...");

                    if (fails > 3)
                    {
                        Console.WriteLine($"WARNING: 3 fails after scanning pages. Maybe we're at the end?");
                        return;
                    }

                    Console.WriteLine($"WARNING: Retry due to potential server error");

                    await Task.Delay(fails * 1000);
                }

                await Task.Delay(100);
                page++;
#if (PARTIAL_DOWNLOAD)
                if (page >= 1)
                {
                    break;
                }
            }
#endif
        }

        /// <summary>
        /// Transforms an URL to a Fileid
        /// </summary>
        /// <param name="pageid">The page id</param>
        /// <param name="targetAccount">The target account</param>
        /// <returns>a task for performing the transformation</returns>
        private static async Task<bool> GetPage(int pageid, string targetAccount)
        {
            try
            {
                using (var client = new HttpClient())
                {
                    var response = await client.GetStringAsync($"https://steamcommunity.com/profiles/{targetAccount}/screenshots?p={pageid}&browsefilter=myfiles&view=grid&privacy=30");
                    var matches = Regex.Matches(response, "steamcommunity\\.com/sharedfiles/filedetails/\\?id\\=([0-9]+?)\"", RegexOptions.Singleline | RegexOptions.IgnoreCase);

                    if (matches.Count == 0)
                        return false;

                    foreach (Match match in matches)
                    {
                        FILES.Add(ulong.Parse(match.Groups[1].Value));
                    }
                }

                return true;
            }
            catch (System.Exception e)
            {
                Console.Error.WriteLine(e.Message);
                return false;
            }
        }

        /// <summary>
        /// Downloads all files and writes images to disk
        /// </summary>
        /// <returns>A task for performing the image download</returns>
        static async Task DownloadImages()
        {
            var tasks = new List<Task>();
#if PARTIAL_DOWNLOAD
            // only take first 10 files if we're partially downloading for tests
            FILES = FILES.Take(10).ToList();
#endif
            foreach (var file in FILES)
            {
                var t = DownloadImage(file);

                tasks.Add(t);

                // We hard cap the amount of tasks that can be out at any time at TASK_LIMIT
                while (tasks.Count > TASK_LIMIT)
                {
                    await Task.WhenAny(tasks);
                    tasks.RemoveAll(x => x.IsCompleted);
                }
            }

            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Downloads a file to disk
        /// </summary>
        /// <param name="file">The file</param>
        /// <param name="retries">The amount of retries for this file</param>
        /// <returns>The task performing the image download</returns>
        private static async Task<bool> DownloadImage(ulong file, int retries = 0)
        {
            if (retries >= MAX_RETRIES)
            {
                return false;
            }

            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("user-agent", USER_AGENT);

                var response = await client.GetStringAsync($"https://steamcommunity.com/sharedfiles/filedetails/?id={file}");
                var matches = Regex.Matches(response, "\\<a href\\=\"(https\\://steamuserimages-a.akamaihd.net/ugc/([A-Z0-9/].+?))\"", RegexOptions.Singleline | RegexOptions.IgnoreCase);

                if (matches.Count == 0)
                {
                    Console.WriteLine($"ERROR - Couldn't find image link for file id: [{file}]");
                    return false;
                }

                var imageUrl = matches.First().Groups[1].Value;
                Console.WriteLine($"[{file}] - downloading {imageUrl}");

                var download = await client.GetAsync(imageUrl);
                var fileId = GetStringBetween(imageUrl, "ugc/", "/");
                var extension = GetFileExtension(download.Content.Headers.GetValues("Content-Type").First());
                var data = await download.Content.ReadAsByteArrayAsync();

                File.WriteAllBytes($"{DOWNLOAD_FOLDER}/{fileId}{extension}", data);

                return true;
            }
            catch (System.Exception e)
            {
                Console.Error.WriteLine(e.Message);
                await Task.Delay(RETRY_DELAY);
                return await DownloadImage(file, retries + 1);
            }
        }

        /// <summary>
        /// Gets the string between a startWord and endWord
        /// </summary>
        /// <param name="s">The string</param>
        /// <param name="startWord">The start word to look for</param>
        /// <param name="endWord">The end word to look for</param>
        /// <returns>The augmented string</returns>
        private static string GetStringBetween(string s, string startWord, string endWord)
        {
            int start = s.IndexOf(startWord) + startWord.Length;
            int end = s.IndexOf(endWord, start);
            return s[start..end];
        }

        /// <summary>
        /// Gets the file extension based on mimeType
        /// </summary>
        /// <param name="mimeType">The mimeType</param>
        /// <returns>The file extension</returns>
        /// <exception cref="ArgumentException">When the mime Type is not in the dictionary</exception>
        private static string GetFileExtension(string mimeType)
        {
            MIME_TO_EXTENSION.TryGetValue(mimeType, out string extension);
            if (extension == null)
            {
                throw new ArgumentException($"Mimetype: {mimeType} not found in the dictionary!");
            }
            return extension;
        }
    }
}
