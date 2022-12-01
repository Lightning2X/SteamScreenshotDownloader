// Uncomment this to only download the first 10 files (for debugging its easier to not download everything)
//#define PARTIAL_DOWNLOAD

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

// Original version credit to Garry Newman (https://github.com/garrynewman)
// Current version by Lightning2x (https://github.com/Lightning2X)

namespace SteamDownloader
{
    class Program
    {

        static List<ulong> FILES = new List<ulong>();
        static long STEAM_ID;
        static string USER_AGENT => "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/80.0.3987.132 Safari/537.36";
        static readonly Dictionary<string, string> MIME_TO_EXTENSION = new Dictionary<string, string>()
        {
            { "image/jpeg", ".jpg" },
            { "image/png", ".png" },
            { "image/gif", ".gif" },
            { "image/webp", ".webp" },
        };
        static int MAX_RETRIES => 2;
        static int RETRY_DELAY => 3000;
        static int TASK_LIMIT => 16;

        static async Task Main(string[] args)
        {
            Console.WriteLine($"Steam Screenshot Downloader V2.0");
            Console.WriteLine();
            Console.WriteLine("Please fill in your Steam64 ID. You can find it by googling steam id finder and entering your URL there. Example Steam64 ID: 76561198053864545");
            Console.WriteLine("Please paste your Steam ID by right clicking or pressing ctrl V:");
            STEAM_ID = ReadSteamId();

            Console.WriteLine($"Downloading screenshots for Steam ID {STEAM_ID}...");
            await DownloadTab("screenshots");
            Console.WriteLine($"Downloading artwork for Steam ID {STEAM_ID}...");
            await DownloadTab("images", "artwork");

            Console.WriteLine($"All done! You can see the files in \"{Path.GetFullPath($"./{STEAM_ID}")}\"");

            Console.WriteLine();

        }

        static async Task DownloadTab(string tab, string cosmeticName = null)
        {
            // Make sure the folder exists
            string name = cosmeticName ?? tab;
            string path = $"./{STEAM_ID}/{name}";
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);

            // Scan all the user's pages and save the files in them to the FILES variable
            await ScanPages(tab);
            Console.WriteLine($"Found {FILES.Count} {name}.");
            // Remove any duplicates
            FILES = FILES.Distinct().ToList();
            if (FILES.Count == 0)
            {
                Console.WriteLine($"Nothing found for tab {name}. Press any key to exit.");
                Console.ReadKey();
                return;
            }

            // Download them all
            await DownloadImages(path);

            FILES.Clear();
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
        static async Task ScanPages(string tab)
        {
            int page = 1;
            while (true)
            {
                Console.WriteLine($"Getting Page {page} ({FILES.Count} screenshots found)");

                int attempts = 0;
                while (!await GetPage(page, STEAM_ID.ToString(), tab))
                {
                    attempts++;
                    if (attempts >= MAX_RETRIES)
                    {
                        Console.WriteLine($"No results found in 3 attempts. Assuming this is the end and terminating scanning.");
                        return;
                    }

                    await Task.Delay(attempts * 1000);
                }

                await Task.Delay(100);
                page++;
#if (PARTIAL_DOWNLOAD)
                if (page >= 1)
                {
                    break;
                }
#endif
            }
        }

        /// <summary>
        /// Transforms an URL to a Fileid
        /// </summary>
        /// <param name="pageid">The page id</param>
        /// <param name="targetAccount">The target account</param>
        /// <returns>a task for performing the transformation</returns>
        private static async Task<bool> GetPage(int pageid, string targetAccount, string tab)
        {
            // Removed the catch for now as its more clear to the user that the program is crashing this way
            using (var client = new HttpClient())
            {
                var response = await client.GetStringAsync($"https://steamcommunity.com/profiles/{targetAccount}/{tab}?p={pageid}&browsefilter=myfiles&view=grid&privacy=30");
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

        /// <summary>
        /// Downloads all files and writes images to disk
        /// </summary>
        /// <returns>A task for performing the image download</returns>
        static async Task DownloadImages(string folder)
        {
            var tasks = new List<Task>();
#if PARTIAL_DOWNLOAD
            // only take first 10 files if we're partially downloading for tests
            FILES = FILES.Take(10).ToList();
#endif
            foreach (var file in FILES)
            {
                var t = DownloadImage(file, folder);

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
        private static async Task<bool> DownloadImage(ulong file, string folder, int retries = 0)
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

                File.WriteAllBytes($"{folder}/{fileId}{extension}", data);

                return true;
            }
            catch (System.Exception e)
            {
                Console.Error.WriteLine(e.Message);
                await Task.Delay(RETRY_DELAY);
                return await DownloadImage(file, folder, retries + 1);
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
