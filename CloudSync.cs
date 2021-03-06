﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Live;
using Microsoft.Live.Controls;
using Windows.Storage;

namespace Breadcrumbs
{
    public class CloudSync
    {
        // Magic number from https://account.live.com/developers/applications/index
        private static readonly string ClientID = "000000004C0FB389";

        // What we name the GPX folder in the user's OneDrive.
        private static readonly string GpxFolderName = "Breadcrumbs";

        private async Task<string> GetGpxFolderId()
        {
            LiveOperationResult r;
            try
            {
                r = await s_liveClient.GetAsync("me/skydrive/files");
            }
            catch (LiveConnectException ex)
            {
                Utils.ShowError(ex);
                return null;
            }

            var files = r.Result["data"] as IList<object>;
            if (files == null)
            {
                Utils.ShowError("Result from OneDrive files query is an unknown type.");
                return null;
            }

            foreach (var folder in files.OfType<IDictionary<string, object>>())
            {
                if (!folder.Keys.Contains("name"))
                {
                    Utils.ShowError("A folder returned from OneDrive has no Name field!");
                    return null;
                }
                else if (string.Equals(folder["name"], GpxFolderName))
                {
                    if (!folder.Keys.Contains("id"))
                    {
                        Utils.ShowError("GPX folder returned from OneDrive has no ID field!");
                        return null;
                    }
                    else
                    {
                        return folder["id"] as string;
                    }
                }
            }

            // If we get to here, no GPX folder was found. Create one!

            var gpxFolder = new Dictionary<string, object>();
            gpxFolder.Add("name", GpxFolderName);

            try
            {
                r = await s_liveClient.PostAsync("me/skydrive", gpxFolder);
            }
            catch (LiveConnectException ex)
            {
                Utils.ShowError(ex);
                return null;
            }

            var newFolder = r.Result as IDictionary<string, object>;
            if (newFolder == null)
            {
                Utils.ShowError("Newly created GPX folder is of unknown type.");
                return null;
            }
            else if (!newFolder.Keys.Contains("id"))
            {
                Utils.ShowError("Newly created GPX folder has no ID field!");
                return null;
            }
            else
            {
                return newFolder["id"] as string;
            }
        }

        private async Task<IList<IDictionary<string, object>>> CloudTreeEnumerate(string folderId, string prefix = "")
        {
            LiveOperationResult r;
            try
            {
                r = await s_liveClient.GetAsync(folderId + "/files");
            }
            catch (LiveConnectException ex)
            {
                Utils.ShowError(ex);
                return null;
            }

            var children = r.Result["data"] as IList<object>;
            if (children == null)
            {
                Utils.ShowError("Result from OneDrive files query is an unknown type.");
                return null;
            }

            var allItems = new List<IDictionary<string, object>>();
            foreach (var child in children.OfType<IDictionary<string, object>>())
            {
                string name = child["name"] as string;

                if (string.Equals(child["type"] as string, "folder"))
                {
                    string childId = child["id"] as string;
                    if (folderId != null)
                    {
                        allItems.AddRange(await CloudTreeEnumerate(childId, prefix + "/" + name));
                    }
                }
                else
                {
                    child["name"] = prefix + "/" + child["name"];
                    allItems.Add(child);
                }
            }

            return allItems;
        }

        private async Task<IEnumerable<IDictionary<string, object>>> GetCloudFiles()
        {
            string gpxFolderId = await GetGpxFolderId();
            if (gpxFolderId == null)
                return null;

            return await CloudTreeEnumerate(gpxFolderId);
        }

        private async Task<IEnumerable<IStorageFile>> LocalTreeEnumerate(IStorageFolder folder)
        {
            var allFiles = new List<IStorageFile>();
            foreach (IStorageItem child in await folder.GetItemsAsync())
            {
                if (child.IsOfType(StorageItemTypes.Folder))
                {
                    allFiles.AddRange(await LocalTreeEnumerate(child as IStorageFolder));
                }
                else
                {
                    allFiles.Add(child as IStorageFile);
                }
            }
            return allFiles;
        }

        private async Task<IEnumerable<IStorageFile>> GetLocalFiles()
        {
            IStorageFolder localGpxFolder = await m_mainVM.GetLocalGpxFolder();
            return await LocalTreeEnumerate(localGpxFolder);
        }

        // Returns the OneDrive ID involved in the copy.
        private async Task<FileContainer> CopyFile(FileContainer src, FileContainer dest)
        {
            if (src.Source == dest.Source)
            {
                throw new ArgumentException("Source and Destination source types (Local vs. OneDrive) need to be different.");
            }

            switch (src.Source)
            {
                case FileContainer.SourceType.Local:
                    {
                        try
                        {
                            LiveOperationResult result;

                            string folderId = await GetGpxFolderId();
                            string[] pathComponents = dest.Path.Substring(1).Split('/');
                            foreach (string pathComponent in pathComponents.Take(pathComponents.Length - 1))
                            {
                                result = await s_liveClient.GetAsync(folderId + "/files?filter=folders");

                                var subFolders = result.Result["data"] as IEnumerable<object>;
                                IDictionary<string, object> subfolder = subFolders.OfType<IDictionary<string, object>>()
                                    .SingleOrDefault(item => (item["name"] as string).Equals(pathComponent, StringComparison.OrdinalIgnoreCase));

                                if (subfolder == null)
                                {
                                    // Create it
                                    var props = new Dictionary<string, object> { { "name", pathComponent } };
                                    result = await s_liveClient.PostAsync(folderId, props);
                                    folderId = result.Result["id"] as string;
                                }
                                else
                                {
                                    // TODO: these should be saved in a shared cache somewhere, so that we only have to discover paths once.
                                    folderId = subfolder["id"] as string;
                                }
                            }

                            using (Stream s = await src.StorageFile.OpenStreamForReadAsync())
                            {
                                result = await s_liveClient.UploadAsync(folderId, Path.GetFileName(dest.Path), s, OverwriteOption.Overwrite);
                            }

                            // The result only contains the ID; no other file metadata. Do another GET to get the rest.
                            string id = result.Result["id"] as string;
                            result = await s_liveClient.GetAsync(id);

                            return new FileContainer()
                            {
                                Source = FileContainer.SourceType.OneDrive,
                                Path = src.Path,
                                MTime = DateTime.Parse(result.Result["updated_time"] as string, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime(),
                                OneDriveId = result.Result["id"] as string
                            };
                        }
                        catch (TaskCanceledException)
                        {
                            Utils.ShowError("Upload cancelled.");
                        }
                        catch (LiveConnectException ex)
                        {
                            Utils.ShowError(ex, src.Path);
                        }
                    }
                    break;

                case FileContainer.SourceType.OneDrive:
                    {
                        try
                        {
                            LiveDownloadOperationResult download = await s_liveClient.DownloadAsync(src.OneDriveId + "/content/");
                            IStorageFolder localGpxFolder = await m_mainVM.GetLocalGpxFolder();
                            IStorageFile newFile = await localGpxFolder.CreatePathAsync(dest.Path.Substring(1), CreationCollisionOption.ReplaceExisting);
                            using (Stream destStream = await newFile.OpenStreamForWriteAsync())
                            {
                                download.Stream.CopyTo(destStream);
                            }
                            return new FileContainer()
                            {
                                Source = FileContainer.SourceType.Local,
                                Path = dest.Path,
                                MTime = (await newFile.GetBasicPropertiesAsync()).DateModified.DateTime.ToUniversalTime(),
                                StorageFile = newFile
                            };
                        }
                        catch (TaskCanceledException)
                        {
                            Utils.ShowError("Download cancelled.");
                        }
                        catch (Exception ex)
                        {
                            Utils.ShowError(ex, src.Path);
                        }
                    }
                    break;
            }

            return null;
        }

        private async Task<FileContainer> CopyFile(FileContainer src)
        {
            return await CopyFile(src, new FileContainer()
            {
                Path = src.Path,
                Source = (src.Source == FileContainer.SourceType.Local) ? FileContainer.SourceType.OneDrive
                                                                        : FileContainer.SourceType.Local,
                MTime = src.MTime,
                OneDriveId = null,
                StorageFile = null,
            });
        }

        private async Task<FileContainer> RenameFile(FileContainer src, string newName)
        {
            System.Diagnostics.Debug.Assert(newName.IndexOf('/') == -1);

            // Path.GetDirectoryName doesn't work well with forward-slashes.
            string[] parts = src.Path.Split('/');
            string newPath = string.Join("/", parts.Take(parts.Length - 1)) + '/' + newName;

            switch (src.Source)
            {
                case FileContainer.SourceType.Local:
                    await src.StorageFile.RenameAsync(newName);
                    return new FileContainer()
                    {
                        Source = FileContainer.SourceType.Local,
                        Path = newPath,
                        MTime = (await src.StorageFile.GetBasicPropertiesAsync()).DateModified.DateTime.ToUniversalTime(),
                        StorageFile = src.StorageFile
                    };

                case FileContainer.SourceType.OneDrive:
                    {
                        var props = new Dictionary<string, object>();
                        props.Add("name", newName);
                        LiveOperationResult result;
                        result = await s_liveClient.PutAsync(src.OneDriveId, props);
                        string mTimeStr = result.Result["updated_time"] as string;
                        string newId = result.Result["id"] as string;
                        return new FileContainer()
                        {
                            Source = FileContainer.SourceType.OneDrive,
                            Path = newPath,
                            MTime = DateTime.Parse(mTimeStr, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime(),
                            OneDriveId = newId
                        };
                    }
            }
            return null;
        }

        private async Task DeleteFile(FileContainer file)
        {
            switch (file.Source)
            {
                case FileContainer.SourceType.Local:
                    await file.StorageFile.DeleteAsync();
                    break;

                case FileContainer.SourceType.OneDrive:
                    await s_liveClient.DeleteAsync(file.OneDriveId);
                    break;
            }
        }

        private string MakeUniqueFilename(FileContainer file)
        {
            var sb = new StringBuilder(Path.GetFileNameWithoutExtension(file.Path));
            sb.Append(" (");
            sb.Append(file.MTime.ToUniversalTime().ToString("yyyy-MM-dd-HH-mm-ss"));
            sb.Append(")");
            sb.Append(Path.GetExtension(file.Path));
            return sb.ToString();
        }

        private async Task<string> GetLocalPrefix()
        {
            return (await m_mainVM.GetLocalGpxFolder()).Path;
        }

        public async Task<Summary> Synchronize(Action<double, string> progressUpdate = null)
        {
            if (s_liveClient == null)
            {
                return new Summary()
                {
                    Uploaded = 0,
                    Downloaded = 0,
                    DeletedLocal = 0,
                    DeletedCloud = 0,
                    UpToDate = 0
                };
            }

            if (progressUpdate == null)
            {
                // Replace null with a simple no-op so we don't have to do a null check every time.
                progressUpdate = (a, b) => { };
            }

            int downloaded = 0;
            int uploaded = 0;
            int deletedLocal = 0;
            int deletedCloud = 0;
            int upToDate = 0;

            var cloudFiles = new List<FileContainer>();
            var localFiles = new List<FileContainer>();

            string localPrefix = await GetLocalPrefix();

            progressUpdate(-1, "Getting list of OneDrive files.");
            IEnumerable<IDictionary<string, object>> cloudResults = await GetCloudFiles();
            foreach (var child in cloudResults)
            {
                var name = child["name"] as string;
                var mtimeStr = child["updated_time"] as string;
                var mtime = DateTime.Parse(mtimeStr, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
                var id = child["id"] as string;
                cloudFiles.Add(new FileContainer()
                {
                    Path = name,
                    Source = FileContainer.SourceType.OneDrive,
                    MTime = mtime,
                    OneDriveId = id
                });
            }

            progressUpdate(-1, "Getting list of local files.");
            foreach (var child in await GetLocalFiles())
            {
                System.Diagnostics.Debug.Assert(child.Path.StartsWith(localPrefix));
                var name = child.Path.Substring(localPrefix.Length).Replace('\\', '/');
                var mtime = (await child.GetBasicPropertiesAsync()).DateModified.DateTime.ToUniversalTime();
                localFiles.Add(new FileContainer()
                {
                    Path = name,
                    Source = FileContainer.SourceType.Local,
                    MTime = mtime,
                    StorageFile = child
                });
            }

            var pathsSeen = new HashSet<string>();
            CloudFileMap fileMap = m_fileMap.Value;

            var deleteLocal = new List<FileContainer>();
            var deleteCloud = new List<FileContainer>();
            var upload = new List<FileContainer>();
            var download = new List<FileContainer>();
            var collision = new List<Tuple<FileContainer, FileContainer>>();

            await Utils.LockStepAsync(localFiles, cloudFiles,
                orderBy: file => file.Path,
                runInOrder: true,
                action: async (localFile, cloudFile) =>
                {
                    if (cloudFile == null)
                    {
                        CloudFileMap.Mapping mapping = fileMap.GetMapping(localFile.Path);

                        if ((mapping != null) && (localFile.MTime <= mapping.LocalModifiedTime))
                        {
                            // We have a mapping for the file, but the cloud version is gone.
                            // Unless the local file is also updated from when we last saw it, delete the local file.
                            deleteLocal.Add(localFile);
                        }
                        else
                        {
                            upload.Add(localFile);
                        }
                    }
                    else if (localFile == null)
                    {
                        CloudFileMap.Mapping mapping = fileMap.GetMapping(cloudFile.Path);

                        if ((mapping != null) && (cloudFile.MTime <= mapping.OneDriveModifiedTime))
                        {
                            // We have a mapping for the file, but the local version is gone.
                            // Unless the cloud file is also updated from when we last saw it, delete the cloud file.
                            deleteCloud.Add(cloudFile);
                        }
                        else
                        {
                            download.Add(cloudFile);
                        }
                    }
                    else if (localFile != null && cloudFile != null)
                    {
                        System.Diagnostics.Debug.Assert(localFile.Path.Equals(cloudFile.Path, StringComparison.OrdinalIgnoreCase));
                        CloudFileMap.Mapping mapping = fileMap.GetMapping(localFile.Path);

                        if (mapping == null)
                        {
                            // Filename collision, and we don't have a mapping (i.e. it's a never-before-seen file).
                            // Rename the local file to a name based on its last modified time.
                            FileContainer local2 = await RenameFile(localFile, MakeUniqueFilename(localFile));

                            // Copy both files.
                            upload.Add(local2);
                            download.Add(cloudFile);
                        }
                        else if (localFile.MTime != mapping.LocalModifiedTime || cloudFile.MTime != mapping.OneDriveModifiedTime)
                        {
                            collision.Add(new Tuple<FileContainer, FileContainer>(localFile, cloudFile));
                        }
                        else
                        {
                            pathsSeen.Add(localFile.Path);
                            upToDate++;
                        }
                    }
                }); // LockStepAsync

            double total = deleteLocal.Count + deleteCloud.Count + upload.Count + download.Count + collision.Count;
            double progress = 0;

            foreach (var localFile in deleteLocal)
            {
                progressUpdate(progress++ / total, "Deleting " + localFile.Path);
                await DeleteFile(localFile);
                await fileMap.RemoveMapping(localFile.Path);
                deletedLocal++;
            }

            foreach (var cloudFile in deleteCloud)
            {
                progressUpdate(progress++ / total, "Deleting " + cloudFile.Path);
                await DeleteFile(cloudFile);
                await fileMap.RemoveMapping(cloudFile.Path);
                deletedCloud++;
            }

            foreach (var localFile in upload)
            {
                progressUpdate(progress++ / total, "Uploading " + localFile.Path);
                FileContainer cloudCopy = await CopyFile(localFile);
                pathsSeen.Add(localFile.Path);
                uploaded++;

                CloudFileMap.Mapping mapping = fileMap.GetMapping(localFile.Path);
                if (mapping == null)
                {
                    await fileMap.AddMapping(localFile.Path, localFile.MTime, cloudCopy.OneDriveId, cloudCopy.MTime);
                }
                else
                {
                    mapping.OneDriveId = cloudCopy.OneDriveId;
                    mapping.OneDriveModifiedTime = cloudCopy.MTime; // update the OneDrive mtime
                }
            }

            foreach (var cloudFile in download)
            {
                progressUpdate(progress++ / total, "Downloading " + cloudFile.Path);
                FileContainer localCopy = await CopyFile(cloudFile);
                pathsSeen.Add(cloudFile.Path);
                downloaded++;

                CloudFileMap.Mapping mapping = fileMap.GetMapping(cloudFile.Path);
                if (mapping == null)
                {
                    await fileMap.AddMapping(localCopy.Path, localCopy.MTime, cloudFile.OneDriveId, cloudFile.MTime);
                }
                else
                {
                    mapping.LocalModifiedTime = localCopy.MTime;
                }
            }

            foreach (var pair in collision)
            {
                // One or both of the files was updated. Figure out which one is newer, and copy that over the old one.
                // Note that we can't just compare MTimes alone, as OneDrive file MTimes are set to their upload time.

                FileContainer localFile = pair.Item1;
                FileContainer cloudFile = pair.Item2;
                CloudFileMap.Mapping mapping = fileMap.GetMapping(localFile.Path);

                DateTime localComparisonTime = DateTime.MinValue;
                DateTime cloudComparisonTime = DateTime.MinValue;
                if (localFile.MTime != mapping.LocalModifiedTime)
                    localComparisonTime = localFile.MTime;
                if (cloudFile.MTime != mapping.OneDriveModifiedTime)
                    cloudComparisonTime = cloudFile.MTime;

                int c = localComparisonTime.CompareTo(cloudComparisonTime);
                if (c == 0)
                {
                    // Both files have the same time for comparison purposes. Just update the mapping.
                    progressUpdate(progress++ / total, string.Empty);
                    mapping.LocalModifiedTime = localFile.MTime;
                    mapping.OneDriveModifiedTime = cloudFile.MTime;
                    upToDate++;
                }
                else
                {
                    FileContainer newer, older;
                    if (c > 0)
                    {
                        newer = localFile;
                        older = cloudFile;
                        progressUpdate(progress++ / total, "Uploading " + newer.Path);
                        uploaded++;
                    }
                    else
                    {
                        newer = cloudFile;
                        older = localFile;
                        progressUpdate(progress++ / total, "Downloading " + newer.Path);
                        downloaded++;
                    }

                    FileContainer result = await CopyFile(newer, older);
                    if (result.Source == FileContainer.SourceType.Local)
                    {
                        mapping.LocalModifiedTime = result.MTime;
                    }
                    else
                    {
                        mapping.OneDriveId = result.OneDriveId;
                        mapping.OneDriveModifiedTime = result.MTime;
                    }
                }
            }

            progressUpdate(1.0, "Finishing up...");

            // Remove paths that weren't seen on the local side or the cloud side.
            await fileMap.RemoveMappingsWhere(mapping => !pathsSeen.Contains(mapping.Path));

            return new Summary()
            {
                Uploaded = uploaded,
                Downloaded = downloaded,
                DeletedLocal = deletedLocal,
                DeletedCloud = deletedCloud,
                UpToDate = upToDate
            };
        }

        // This must be run from the UI thread prior to calling Synchronize().
        public static async Task<LiveConnectSession> Authenticate()
        {
            if (s_liveClient != null)
            {
                return s_liveClient.Session;
            }

            try
            {
                var authClient = new LiveAuthClient(ClientID);
                LiveLoginResult result = await authClient.LoginAsync(
                    new string[] {
                        // Scopes and permsissions.
                        // See http://msdn.microsoft.com/en-us/library/live/hh243646.aspx
                        "wl.skydrive_update", // Read-write access to user's OneDrive
                        "wl.signin",          // Automatic signin, so they don't have to type a password.
                    });
                if (result.Status == LiveConnectSessionStatus.Connected)
                {
                    s_liveClient = new LiveConnectClient(result.Session);
                    return result.Session;
                }
            }
            catch (Exception ex)
            {
                Utils.ShowError(ex);
            }
            return null;
        }
        static LiveConnectClient s_liveClient;

        public CloudSync(ViewModels.MainViewModel mainVM)
        {
            m_mainVM = mainVM;
            m_fileMap = new Lazy<CloudFileMap>(() =>
                Task.Run(async () =>
                    {
                        StorageFolder local = ApplicationData.Current.LocalFolder;
                        IStorageFile fileMapFile = null;
                        try
                        {
                            fileMapFile = await local.GetFileAsync("CloudFileMap.xml");
                        }
                        catch (FileNotFoundException)
                        {
                            // Do nothing; handle below.
                        }

                        if (fileMapFile == null)
                        {
                            fileMapFile = await local.CreateFileAsync("CloudFileMap.xml");
                        }

                        return await CloudFileMap.CreateInstance(fileMapFile);
                    }).Result);
        }

        private ViewModels.MainViewModel m_mainVM;
        private Lazy<CloudFileMap> m_fileMap;

        private class FileContainer
        {
            public enum SourceType
            {
                Local,
                OneDrive
            }

            public string Path;
            public SourceType Source;
            public IStorageFile StorageFile;
            public string OneDriveId;
            public DateTime MTime;

            public override string ToString()
            {
                return Source.ToString() + ":" + Path;
            }
        }

        public struct Summary
        {
            public int Uploaded;
            public int Downloaded;
            public int DeletedLocal;
            public int DeletedCloud;
            public int UpToDate;
        }
    }
}
