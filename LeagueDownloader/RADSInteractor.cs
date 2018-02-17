﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using Fantome.Libraries.League.IO.ReleaseManifest;
using Fantome.Libraries.League.IO.RiotArchive;
using static Fantome.Libraries.League.IO.ReleaseManifest.ReleaseManifestFile.DeployMode;
using System.IO.Compression;
using LeagueDownloader.Solution;
using System.Web;

namespace LeagueDownloader
{
    public class RADSInteractor
    {
        private static readonly string LeagueCDN = "http://l3cdn.riotgames.com";

        public string Platform { get; private set; }

        private WebClient webClient = new WebClient();

        public RADSInteractor(string platform)
        {
            this.Platform = platform;
        }

        public void InstallSolution(string directory, string solutionName, string solutionVersion, string localization, uint? deployMode)
        {
            // Downloading solution manifest
            Console.WriteLine("Downloading solution manifest for release {0}", solutionVersion);
            var solutionFolder = String.Format("{0}/RADS/solutions/{1}/releases/{2}", directory, solutionName, solutionVersion);
            System.IO.Directory.CreateDirectory(solutionFolder);
            webClient.DownloadFile(
                String.Format("{0}/releases/{1}/solutions/{2}/releases/{3}/solutionmanifest", LeagueCDN, Platform, solutionName, solutionVersion),
                solutionFolder + "/solutionmanifest");
            var solutionManifest = new SolutionManifest(File.ReadAllLines(solutionFolder + "/solutionmanifest"));
            LocalizedEntry localizedEntry = solutionManifest.LocalizedEntries.Find(x => x.Name.Equals(localization, StringComparison.InvariantCultureIgnoreCase));
            if (localizedEntry != null)
            {
                // Creating configuration manifest
                var configurationManifest = new ConfigurationManifest(localizedEntry);
                configurationManifest.Write(solutionFolder + "/configurationmanifest");

                // Downloading each project
                foreach (SolutionProject project in localizedEntry.Projects)
                    InstallProject(directory, project.Name, project.Version, deployMode, solutionName, solutionVersion);

                File.Create(solutionFolder + "/S_OK").Close();
            }
        }

        public void InstallProjectExperimental(string directory, string projectName, string projectVersion, uint? deployMode, string solutionName = null, string solutionVersion = null)
        {
            var projectsURL = String.Format("{0}/releases/{1}/projects/{2}", LeagueCDN, Platform, projectName);

            var tempFolder = String.Format("{0}/RADS/temp/patching/{1}/{2}", directory, projectName, projectVersion);
            var projectFolder = String.Format("{0}/RADS/projects/{1}", directory, projectName);
            var releaseFolder = String.Format("{0}/releases/{1}", projectFolder, projectVersion);
            var deployFolder = releaseFolder + "/deploy";
            var managedFilesFolder = projectFolder + "/managedfiles";
            var archivesFolder = projectFolder + "/filearchives";
            Directory.CreateDirectory(tempFolder);
            Directory.CreateDirectory(deployFolder);
            Directory.CreateDirectory(managedFilesFolder);
            Directory.CreateDirectory(archivesFolder);

            string solutionDeployFolder = null;
            if (solutionName != null)
                solutionDeployFolder = String.Format("{0}/RADS/solutions/{1}/releases/{2}/deploy", directory, solutionName, solutionVersion);

            // Getting release manifest
            Console.WriteLine("Downloading manifest for project {0}, release {1}...", projectName, projectVersion);
            var currentProjectURL = String.Format("{0}/releases/{1}", projectsURL, projectVersion);
            webClient.DownloadFile(currentProjectURL + "/releasemanifest", releaseFolder + "/releasemanifest");
            var releaseManifest = new ReleaseManifestFile(releaseFolder + "/releasemanifest");

            // Downloading archives
            int archiveCount = 0;
            string currentArchiveName = "BIN_0x" + archiveCount.ToString("00000000");
            var currentArchiveURL = String.Format("{0}/packages/files/{1}", currentProjectURL, currentArchiveName);
            while (RemoteArchiveExists(currentArchiveURL))
            {
                string filePath = tempFolder + "/" + currentArchiveName;
                if (!File.Exists(filePath))
                    webClient.DownloadFile(currentArchiveURL, filePath);
                archiveCount++;
                currentArchiveName = "BIN_0x" + archiveCount.ToString("00000000");
                currentArchiveURL = String.Format("{0}/packages/files/{1}", currentProjectURL, currentArchiveName);
            }

            var files = new List<ReleaseManifestFileEntry>();
            EnumerateManifestFolderFiles(releaseManifest.Project, files);
            List<ReleaseManifestFileEntry> compressedEntries = files.FindAll(x => x.SizeCompressed > 0);
            List<ReleaseManifestFileEntry> rawEntries = files.FindAll(x => x.SizeCompressed == 0);
            rawEntries.Sort((x, y) => (x.SizeRaw.CompareTo(y.SizeRaw)));
            List<ReleaseManifestFileEntry> entries = new List<ReleaseManifestFileEntry>();
            for (int i = 0; i < archiveCount; i++)
            {
                currentArchiveName = "BIN_0x" + i.ToString("00000000");
                using (BinaryReader br = new BinaryReader(File.Open(tempFolder + "/" + currentArchiveName, FileMode.Open)))
                {
                    while (br.BaseStream.Position < br.BaseStream.Length)
                    {
                        if (GetNextZlibOffset(br, br.BaseStream.Position) != null)
                        {
                            // Compressed entry here (maybe..?)
                            long endPosition = br.BaseStream.Position;
                            ReleaseManifestFileEntry foundEntry = null;
                            while (foundEntry == null && endPosition < br.BaseStream.Length)
                            {
                                long? nextZlibOffset = GetNextZlibOffset(br, endPosition + 1);
                                long fileSize = 0;
                                if (nextZlibOffset == null)
                                    // Until the end
                                    fileSize = br.BaseStream.Length - br.BaseStream.Position;
                                else
                                {
                                    fileSize = (long)nextZlibOffset - br.BaseStream.Position;
                                    endPosition = (long)nextZlibOffset;
                                }
                                var potentialEntries = compressedEntries.FindAll(x => x.SizeCompressed == fileSize);
                                if (potentialEntries.Count > 0)
                                {
                                    try
                                    {
                                        byte[] decompressed = DecompressZlib(br.ReadBytes((int)fileSize));
                                        foundEntry = potentialEntries.Find(x => x.SizeRaw == decompressed.Length);
                                    }
                                    catch (Exception)
                                    {
                                    }
                                    if (foundEntry == null)
                                        br.BaseStream.Seek(-fileSize, SeekOrigin.Current);
                                }
                            }
                            if (foundEntry != null)
                            {
                                entries.Add(foundEntry);
                            }
                            else
                            {
                                entries = entries;
                            }
                        }
                        else
                        {
                            int a = 0;
                            // Raw entry here
                        }
                    }
                }
            }
        }

        private long? GetNextZlibOffset(BinaryReader br, long startPosition)
        {
            long currentOffset = br.BaseStream.Position;
            br.BaseStream.Seek(startPosition, SeekOrigin.Begin);
            byte? currentByte = null;
            while (br.BaseStream.Position < br.BaseStream.Length)
            {
                if (currentByte == 120)
                {
                    currentByte = br.ReadByte();
                    if (currentByte == 156)
                    {
                        long patternPosition = br.BaseStream.Position - 2;
                        br.BaseStream.Seek(currentOffset, SeekOrigin.Begin);
                        return patternPosition;
                    }
                }
                else
                {
                    currentByte = br.ReadByte();
                }
            }
            return null;
        }

        private static bool RemoteArchiveExists(string url)
        {
            try
            {
                HttpWebRequest request = WebRequest.Create(url) as HttpWebRequest;
                request.Method = "HEAD";
                HttpWebResponse response = request.GetResponse() as HttpWebResponse;
                response.Close();
                return (response.StatusCode == HttpStatusCode.OK);
            }
            catch
            {
                return false;
            }
        }

        public void InstallProject(string directory, string projectName, string projectVersion, uint? deployMode, string solutionName = null, string solutionVersion = null)
        {
            var projectsURL = String.Format("{0}/releases/{1}/projects/{2}", LeagueCDN, Platform, projectName);

            var projectFolder = String.Format("{0}/RADS/projects/{1}", directory, projectName);
            var releaseFolder = String.Format("{0}/releases/{1}", projectFolder, projectVersion);
            var deployFolder = releaseFolder + "/deploy";
            var managedFilesFolder = projectFolder + "/managedfiles";
            var archivesFolder = projectFolder + "/filearchives";
            Directory.CreateDirectory(deployFolder);
            Directory.CreateDirectory(managedFilesFolder);
            Directory.CreateDirectory(archivesFolder);

            string solutionDeployFolder = null;
            if (solutionName != null)
                solutionDeployFolder = String.Format("{0}/RADS/solutions/{1}/releases/{2}/deploy", directory, solutionName, solutionVersion);

            // Getting release manifest
            Console.WriteLine("Downloading manifest for project {0}, release {1}...", projectName, projectVersion);
            var currentProjectURL = String.Format("{0}/releases/{1}", projectsURL, projectVersion);
            webClient.DownloadFile(currentProjectURL + "/releasemanifest", releaseFolder + "/releasemanifest");
            var releaseManifest = new ReleaseManifestFile(releaseFolder + "/releasemanifest");

            // Downloading files
            var files = new List<ReleaseManifestFileEntry>();
            EnumerateManifestFolderFiles(releaseManifest.Project, files);
            files.Sort((x, y) => (x.Version.CompareTo(y.Version)));

            string currentArchiveVersion = null;
            RAF currentRAF = null;
            foreach (ReleaseManifestFileEntry file in files)
            {
                string fileFullPath = file.GetFullPath();
                string fileVersion = GetReleaseString(file.Version);
                Console.WriteLine("Downloading file {0}/{1}", fileVersion, fileFullPath);
                bool compressed = false;
                var fileURL = String.Format("{0}/releases/{1}/files/{2}", projectsURL, fileVersion, EncodeFilePath(fileFullPath));
                if (file.SizeCompressed > 0)
                {
                    fileURL += ".compressed";
                    compressed = true;
                }

                byte[] fileData;
                try
                {
                    fileData = webClient.DownloadData(fileURL);
                    // Change deploy mode if specified
                    if (deployMode != null)
                        file.DeployMode = (ReleaseManifestFile.DeployMode)deployMode;

                    if (file.DeployMode == RAFCompressed || file.DeployMode == RAFRaw)
                    {
                        // File has to be put in a RAF
                        if (currentRAF == null || currentArchiveVersion != fileVersion)
                        {
                            currentRAF?.Save();
                            currentRAF?.Dispose();
                            currentArchiveVersion = fileVersion;
                            currentRAF = new RAF(String.Format("{0}/{1}/Archive_1.raf", archivesFolder, fileVersion));
                        }
                        if (compressed)
                            currentRAF.AddFile(fileFullPath, file.DeployMode == RAFCompressed ? fileData : DecompressZlib(fileData), false);
                        else
                            currentRAF.AddFile(fileFullPath, fileData, file.DeployMode == RAFCompressed);
                    }
                    else if (file.DeployMode == Managed)
                    {
                        // File will be in managedfiles folder
                        var filePath = String.Format("{0}/{1}/{2}", managedFilesFolder, fileVersion, fileFullPath);
                        Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                        File.WriteAllBytes(filePath, compressed ? DecompressZlib(fileData) : fileData);
                    }
                    else if (file.DeployMode == Deployed0 || file.DeployMode == Deployed4)
                    {
                        // File will be in deploy folder
                        var deployPath = String.Format("{0}/{1}", deployFolder, fileFullPath);
                        Directory.CreateDirectory(Path.GetDirectoryName(deployPath));
                        byte[] decompressedData = compressed ? DecompressZlib(fileData) : fileData;
                        File.WriteAllBytes(deployPath, decompressedData);
                        if (solutionDeployFolder != null && file.DeployMode == Deployed4)
                        {
                            // File will also be in solution folder
                            var solutionPath = String.Format("{0}/{1}", solutionDeployFolder, fileFullPath);
                            Directory.CreateDirectory(Path.GetDirectoryName(solutionPath));
                            File.WriteAllBytes(solutionPath, decompressedData);
                        }
                    }
                }
                catch (WebException)
                {
                    Console.WriteLine("Error when downloading file {0}/{1}", fileVersion, fileFullPath);
                }
            }
            currentRAF?.Dispose();
            releaseManifest.Save();
            File.Create(releaseFolder + "/S_OK").Close();
        }

        public void ListFiles(string projectName, string projectVersion, string filter = null, string filesRevision = null)
        {
            List<ReleaseManifestFileEntry> files = EnumerateFiles(projectName, ref projectVersion, filter, filesRevision);
            foreach (ReleaseManifestFileEntry file in files)
                Console.WriteLine("{0}/{1}", GetReleaseString(file.Version), file.GetFullPath());
        }

        public void DownloadFiles(string directory, string projectName, string projectVersion, string filter = null, string filesRevision = null)
        {
            var projectsURL = String.Format("{0}/releases/{1}/projects/{2}", LeagueCDN, Platform, projectName);
            List<ReleaseManifestFileEntry> files = EnumerateFiles(projectName, ref projectVersion, filter, filesRevision);
            Console.WriteLine("{0} files to download", files.Count);
            foreach (ReleaseManifestFileEntry file in files)
            {
                string fileFullPath = file.GetFullPath();
                string fileOutputPath = String.Format("{0}/{1}/releases/{2}/{3}", directory, projectName, projectVersion, fileFullPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fileOutputPath));
                string fileVersion = GetReleaseString(file.Version);
                Console.WriteLine("Downloading file {0}/{1}", fileVersion, fileFullPath);
                bool compressed = false;
                var fileURL = String.Format("{0}/releases/{1}/files/{2}", projectsURL, fileVersion, EncodeFilePath(fileFullPath));
                if (file.SizeCompressed > 0)
                {
                    fileURL += ".compressed";
                    compressed = true;
                }
                try
                {
                    byte[] fileData = webClient.DownloadData(fileURL);
                    if (compressed)
                        fileData = DecompressZlib(fileData);
                    File.WriteAllBytes(fileOutputPath, fileData);
                }
                catch (WebException)
                {

                }
            }
        }


        private List<ReleaseManifestFileEntry> EnumerateFiles(string projectName, ref string projectVersion, string filter = null, string filesRevision = null)
        {
            if (projectVersion == "LATEST")
                projectVersion = GetLatestRelease(projectName);

            if (filesRevision == "LATEST")
                filesRevision = projectVersion;

            var releaseURL = String.Format("{0}/releases/{1}/projects/{2}/releases/{3}", LeagueCDN, Platform, projectName, projectVersion);
            webClient.DownloadFile(releaseURL + "/releasemanifest", "tempmanifest");
            var releaseManifest = new ReleaseManifestFile("tempmanifest");
            File.Delete("tempmanifest");

            var files = new List<ReleaseManifestFileEntry>();
            EnumerateManifestFolderFiles(releaseManifest.Project, files);
            if (filesRevision != null)
            {
                uint revisionValue = GetReleaseValue(filesRevision);
                files = files.FindAll(x => x.Version == revisionValue);
            }
            if (filter != null)
            {
                Predicate<ReleaseManifestFileEntry> predicate = x => x.GetFullPath().Equals(filter, StringComparison.InvariantCultureIgnoreCase);
                if (filter.EndsWith("/"))
                    predicate = x => (x.Folder.GetFullPath() + "/").StartsWith(filter, StringComparison.InvariantCultureIgnoreCase);
                files = files.FindAll(predicate);
            }
            return files;
        }

        private string GetLatestRelease(string projectName)
        {
            var releaseListingURL = String.Format("{0}/releases/{1}/projects/{2}/releases/releaselisting", LeagueCDN, Platform, projectName);
            using (var sr = new StringReader(webClient.DownloadString(releaseListingURL)))
                return sr.ReadLine();
        }

        private static byte[] DecompressZlib(byte[] inputData)
        {
            byte[] data = new byte[inputData.Length - 6];
            using (MemoryStream ms = new MemoryStream(inputData))
            {
                ms.Seek(2, SeekOrigin.Begin);
                ms.Read(data, 0, data.Length);
            }
            return Inflate(data);
        }

        private static byte[] Inflate(byte[] compressedData)
        {
            byte[] decompressedData = null;
            using (MemoryStream compressedStream = new MemoryStream(compressedData))
            {
                using (MemoryStream rawStream = new MemoryStream())
                {
                    using (DeflateStream decompressionStream = new DeflateStream(compressedStream, CompressionMode.Decompress))
                    {
                        decompressionStream.CopyTo(rawStream);
                    }
                    decompressedData = rawStream.ToArray();
                }
            }
            return decompressedData;
        }

        private static string EncodeFilePath(string fileFullPath)
        {
            string[] split = fileFullPath.Split('/');
            for (int i = 0; i < split.Length; i++)
            {
                split[i] = HttpUtility.UrlEncode(split[i]);
            }
            return String.Join("/", split);
        }

        private static void EnumerateManifestFolderFiles(ReleaseManifestFolderEntry folder, List<ReleaseManifestFileEntry> currentList)
        {
            currentList.AddRange(folder.Files);
            foreach (ReleaseManifestFolderEntry subFolder in folder.Folders)
                EnumerateManifestFolderFiles(subFolder, currentList);
        }

        private static string GetReleaseString(uint releaseValue)
        {
            return String.Format("{0}.{1}.{2}.{3}", (releaseValue & 0xFF000000) >> 24, (releaseValue & 0x00FF0000) >> 16, (releaseValue & 0x0000FF00) >> 8, releaseValue & 0x000000FF);
        }

        private static uint GetReleaseValue(string releaseString)
        {
            string[] releaseValues = releaseString.Split('.');
            return (uint)((Byte.Parse(releaseValues[0]) << 24) | (Byte.Parse(releaseValues[1]) << 16) | (Byte.Parse(releaseValues[2]) << 8) | Byte.Parse(releaseValues[3]));
        }
    }
}