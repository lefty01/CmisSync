//   CmisSync, a collaboration and sharing tool.
//   Copyright (C) 2010  Hylke Bons <hylkebons@gmail.com>
//
//   This program is free software: you can redistribute it and/or modify
//   it under the terms of the GNU General Public License as published by
//   the Free Software Foundation, either version 3 of the License, or
//   (at your option) any later version.
//
//   This program is distributed in the hope that it will be useful,
//   but WITHOUT ANY WARRANTY; without even the implied warranty of
//   MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//   GNU General Public License for more details.
//
//   You should have received a copy of the GNU General Public License
//   along with this program. If not, see <http://www.gnu.org/licenses/>.


using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;

using CmisSync.Lib;
using CmisSync.Lib.Cmis;
using log4net;

namespace CmisSync
{

    public abstract class ControllerBase : ActivityListener
    {
        protected static readonly ILog Logger = LogManager.GetLogger(typeof(ControllerBase));
        private bool firstRun;
        private RepoInfo repoInfo;

        public RepoBase[] Repositories
        {
            get
            {
                lock (this.repo_lock)
                    return this.repositories.GetRange(0, this.repositories.Count).ToArray();
            }
        }

        public bool RepositoriesLoaded { get; private set; }

        private List<RepoBase> repositories = new List<RepoBase>();
        public string FoldersPath { get; private set; }

        public double ProgressPercentage = 0.0;
        public string ProgressSpeed = "";

        public event ShowSetupWindowEventHandler ShowSetupWindowEvent = delegate { };
        public delegate void ShowSetupWindowEventHandler(PageType page_type);

        public event Action ShowAboutWindowEvent = delegate { };
        public event Action ShowEventLogWindowEvent = delegate { };

        public event FolderFetchedEventHandler FolderFetched = delegate { };
        public delegate void FolderFetchedEventHandler(string remote_url, string[] warnings);

        public event FolderFetchErrorHandler FolderFetchError = delegate { };
        public delegate void FolderFetchErrorHandler(string remote_url, string[] errors);

        public event FolderFetchingHandler FolderFetching = delegate { };
        public delegate void FolderFetchingHandler(double percentage);

        public event Action FolderListChanged = delegate { };


        public event Action OnIdle = delegate { };
        public event Action OnSyncing = delegate { };
        public event Action OnError = delegate { };


        public event NotificationRaisedEventHandler NotificationRaised = delegate { };
        public delegate void NotificationRaisedEventHandler(ChangeSet change_set);

        public event AlertNotificationRaisedEventHandler AlertNotificationRaised = delegate { };
        public delegate void AlertNotificationRaisedEventHandler(string title, string message);


        public bool FirstRun
        {
            get
            {
                return firstRun;
            }
        }

        public List<string> Folders
        {
            get
            {
                List<string> folders = ConfigManager.CurrentConfig.Folders;
                folders.Sort();

                return folders;
            }
        }

        public List<string> UnsyncedFolders
        {
            get
            {
                List<string> unsynced_folders = new List<string>();

                foreach (RepoBase repo in Repositories)
                {
                    repo.HasUnsyncedChanges();
                }

                return unsynced_folders;
            }
        }

        public bool NotificationsEnabled
        {
            get
            {
                string notifications_enabled = ConfigManager.CurrentConfig.GetConfigOption("notifications");

                if (string.IsNullOrEmpty(notifications_enabled))
                {
                    ConfigManager.CurrentConfig.SetConfigOption("notifications", bool.TrueString);
                    return true;

                }
                else
                {
                    return notifications_enabled.Equals(bool.TrueString);
                }
            }
        }


        public abstract string EventLogHTML { get; }
        public abstract string DayEntryHTML { get; }
        public abstract string EventEntryHTML { get; }

        // Enables CmisSync to start automatically at login
        public abstract void CreateStartupItem();

        // Installs the CmisSync:// protocol handler
        public abstract void InstallProtocolHandler();

        // Adds the CmisSync folder to the user's
        // list of bookmarked places
        public abstract void AddToBookmarks();

        // Creates the CmisSync folder in the user's home folder
        public abstract bool CreateCmisSyncFolder();

        // Opens the CmisSync folder or an (optional) subfolder
        public abstract void OpenFolder(string path);

        // Opens a file with the appropriate application
        public abstract void OpenFile(string path);


        private ActivityListener activityListenerAggregator;
        private Fetcher fetcher;
        private FileSystemWatcher watcher;
        private Object repo_lock = new Object();
        private Object check_repos_lock = new Object();


        public ControllerBase()
        {
            activityListenerAggregator = new ActivityListenerAggregator(this);
            FoldersPath = ConfigManager.CurrentConfig.FoldersPath;
        }


        public virtual void Initialize(Boolean firstRun)
        {
            this.firstRun = firstRun;
            InstallProtocolHandler();

            // Create the CmisSync folder and add it to the bookmarks
            if (CreateCmisSyncFolder())
                AddToBookmarks();

            if (firstRun)
            {
                ConfigManager.CurrentConfig.SetConfigOption("notifications", bool.TrueString);
            }

            // Watch the CmisSync folder
            this.watcher = new FileSystemWatcher()
            {
                Filter = "*",
                IncludeSubdirectories = false,
                Path = FoldersPath
            };

            watcher.Deleted += OnFolderActivity;
            watcher.Created += OnFolderActivity;
            watcher.Renamed += OnFolderActivity;

            watcher.EnableRaisingEvents = true;
        }


        public void UIHasLoaded()
        {
            if (firstRun)
            {
                ShowSetupWindow(PageType.Setup);

            }
            else
            {
                new Thread(() =>
                {
                    CheckRepositories();
                    RepositoriesLoaded = true;
                    FolderListChanged();

                }).Start();
            }
        }


        private void AddRepository(string folder_path)
        {
            RepoBase repo = null;
            string folder_name = Path.GetFileName(folder_path);

            RepoInfo repositoryInfo = ConfigManager.CurrentConfig.GetRepoInfo(folder_name);
            repo = new CmisSync.Lib.Sync.CmisRepo(repositoryInfo, activityListenerAggregator);

            repo.ChangesDetected += delegate
            {
                UpdateState();
            };

            repo.SyncStatusChanged += delegate(SyncStatus status)
            {
                if (status == SyncStatus.Idle)
                {
                    ProgressPercentage = 0.0;
                    ProgressSpeed = "";
                }

                UpdateState();
            };

            repo.ProgressChanged += delegate(double percentage, string speed)
            {
                ProgressPercentage = percentage;
                ProgressSpeed = speed;

                UpdateState();
            };

            repo.NewChangeSet += delegate(ChangeSet change_set)
            {
                if (NotificationsEnabled)
                    NotificationRaised(change_set);
            };

            repo.ConflictResolved += delegate
            {
                if (NotificationsEnabled)
                    AlertNotificationRaised("Conflict detected",
                        "Don't worry, CmisSync made a copy of each conflicting file.");
            };

            this.repositories.Add(repo);
            repo.Initialize();
        }


        private void RemoveRepository(string folder_path)
        {
            if (this.repositories.Count > 0)
            {
                for (int i = 0; i < this.repositories.Count; i++)
                {
                    RepoBase repo = this.repositories[i];

                    if (repo.LocalPath.Equals(folder_path))
                    {
                        // Remove Cmis Database File
                        RemoveDatabase(folder_path);

                        repo.Dispose();
                        this.repositories.Remove(repo);
                        repo = null;

                        return;
                    }
                }
            }

            RemoveDatabase(folder_path);
        }

        public void StartOrSuspendRepository(string repoName)
        {
            foreach (RepoBase aRepo in this.repositories)
            {
                if (aRepo.Name == repoName)
                {
                    if (aRepo.Status != SyncStatus.Suspend)
                        aRepo.Suspend();
                    else aRepo.Resume();
                }
            }
        }

        private void RemoveDatabase(string folder_path)
        {
            string databasefile = Path.Combine(ConfigManager.CurrentConfig.ConfigPath, Path.GetFileName(folder_path) + ".cmissync");
            if (File.Exists(databasefile)) File.Delete(databasefile);
        }

        private void CheckRepositories()
        {
            lock (this.check_repos_lock)
            {
                string path = ConfigManager.CurrentConfig.FoldersPath;

                foreach (string folder_path in Directory.GetDirectories(path))
                {
                    string folder_name = Path.GetFileName(folder_path);

                    if (folder_name.Equals(".tmp"))
                        continue;

                    if (ConfigManager.CurrentConfig.GetIdentifierForFolder(folder_name) == null)
                    {
                        string identifier_file_path = Path.Combine(folder_path, ".CmisSync");

                        if (!File.Exists(identifier_file_path))
                            continue;

                        string identifier = File.ReadAllText(identifier_file_path).Trim();

                        if (ConfigManager.CurrentConfig.IdentifierExists(identifier))
                        {
                            RemoveRepository(folder_path);
                            ConfigManager.CurrentConfig.RenameFolder(identifier, folder_name);

                            string new_folder_path = Path.Combine(path, folder_name);
                            AddRepository(new_folder_path);

                            Logger.Info("Controller | Renamed folder with identifier " + identifier + " to '" + folder_name + "'");
                        }
                    }
                }

                foreach (string folder_name in ConfigManager.CurrentConfig.Folders)
                {
                    string folder_path = new Folder(folder_name).FullPath;

                    if (!Directory.Exists(folder_path))
                    {
                        RemoveRepository(folder_path);
                        ConfigManager.CurrentConfig.RemoveFolder(folder_name);

                        Logger.Info("Controller | Removed folder '" + folder_name + "' from config");

                    }
                    else
                    {
                        AddRepository(folder_path);
                    }
                }

                FolderListChanged();
            }
        }


        // Fires events for the current syncing state
        private void UpdateState()
        {
            bool has_unsynced_repos = false;

            foreach (RepoBase repo in Repositories)
            {
                repo.HasUnsyncedChanges();
            }

            if (has_unsynced_repos)
                OnError();
            else
                OnIdle();
        }


        private void ClearFolderAttributes(string path)
        {
            if (!Directory.Exists(path))
                return;

            string[] folders = Directory.GetDirectories(path);

            foreach (string folder in folders)
                ClearFolderAttributes(folder);

            string[] files = Directory.GetFiles(path);

            foreach (string file in files)
                if (!IsSymlink(file))
                    File.SetAttributes(file, FileAttributes.Normal);
        }


        private bool IsSymlink(string file)
        {
            FileAttributes attributes = File.GetAttributes(file);
            return ((attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint);
        }


        public void OnFolderActivity(object o, FileSystemEventArgs args)
        {
            if (Directory.Exists(args.FullPath) && args.ChangeType == WatcherChangeTypes.Created)
                return;

            // CheckRepositories (); // NR Disabled because was creating tons of Cmis objects
        }


        public void StartFetcher(string address, string required_fingerprint,
            string remote_path, string local_path, string announcements_url, bool fetch_prior_history,
            string repository, string path, string user, string password, string localrepopath)
        {
            if (announcements_url != null)
                announcements_url = announcements_url.Trim();

            string tmp_path = ConfigManager.CurrentConfig.TmpPath;

            repoInfo = new RepoInfo(local_path, ConfigManager.CurrentConfig.ConfigPath);
            repoInfo.Address = new Uri(address);
            repoInfo.RemotePath = remote_path;
            repoInfo.RepoID = repository;
            repoInfo.User = user;
            repoInfo.Password = Crypto.Protect(password);
            repoInfo.TargetDirectory = localrepopath;
            repoInfo.PollInterval = 5000;

            fetcher = new Fetcher(repoInfo, activityListenerAggregator);

            this.fetcher.Finished += delegate(bool repo_is_encrypted, bool repo_is_empty, string[] warnings)
            {
                FinishFetcher();
            };

            this.fetcher.Failed += delegate
            {
                FolderFetchError(this.fetcher.RemoteUrl.ToString(), this.fetcher.Errors);
                StopFetcher();
            };

            this.fetcher.ProgressChanged += delegate(double percentage)
            {
                FolderFetching(percentage);
            };

            this.FinishFetcher();
        }


        public void StopFetcher()
        {
            //this.fetcher.Stop();

            if (Directory.Exists(this.fetcher.TargetFolder))
            {
                try
                {
                    Directory.Delete(this.fetcher.TargetFolder, true);
                    Logger.Info("Deleted " + this.fetcher.TargetFolder);

                }
                catch (Exception e)
                {
                    Logger.Info("Failed to delete " + this.fetcher.TargetFolder + ": " + e.Message);
                }
            }

            this.fetcher.Dispose();
            this.fetcher = null;
        }


        public void FinishFetcher(string password)
        {
            //this.fetcher.EnableFetchedRepoCrypto(password);

            this.watcher.EnableRaisingEvents = false;
            FinishFetcher();
            this.watcher.EnableRaisingEvents = true;
        }


        public void FinishFetcher()
        {
            ConfigManager.CurrentConfig.AddFolder(repoInfo);

            FolderFetched(this.fetcher.RemoteUrl.ToString(), this.fetcher.Warnings.ToArray());

            AddRepository(repoInfo.TargetDirectory);

            FolderListChanged();

            this.fetcher.Dispose();
            this.fetcher = null;
        }


        public bool CheckPassword(string password)
        {
            return true;// this.fetcher.IsFetchedRepoPasswordCorrect(password);
        }


        public void ShowSetupWindow(PageType page_type)
        {
            ShowSetupWindowEvent(page_type);
        }


        public void ShowAboutWindow()
        {
            ShowAboutWindowEvent();
        }


        public void ShowEventLogWindow()
        {
            ShowEventLogWindowEvent();
        }


        public void ToggleNotifications()
        {
            bool notifications_enabled = ConfigManager.CurrentConfig.GetConfigOption("notifications").Equals(bool.TrueString);
            ConfigManager.CurrentConfig.SetConfigOption("notifications", (!notifications_enabled).ToString());
        }


        // Format a file size nicely with small caps.
        // Example: 1048576 becomes "1 ᴍʙ"
        public string FormatSize(double byte_count)
        {
            if (byte_count >= 1099511627776)
                return String.Format("{0:##.##} ᴛʙ", Math.Round(byte_count / 1099511627776, 1));
            else if (byte_count >= 1073741824)
                return String.Format("{0:##.##} ɢʙ", Math.Round(byte_count / 1073741824, 1));
            else if (byte_count >= 1048576)
                return String.Format("{0:##.##} ᴍʙ", Math.Round(byte_count / 1048576, 0));
            else if (byte_count >= 1024)
                return String.Format("{0:##.##} ᴋʙ", Math.Round(byte_count / 1024, 0));
            else
                return byte_count.ToString() + " bytes";
        }


        public virtual void Quit()
        {
            foreach (RepoBase repo in Repositories)
                repo.Dispose();

            Environment.Exit(0);
        }

        public void ActivityStarted()
        {
            OnSyncing();
        }

        public void ActivityStopped()
        {
            OnIdle();
        }
    }
}
