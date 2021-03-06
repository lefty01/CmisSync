﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;

namespace CmisSync.Lib
{
    public class RepoInfo
    {
        // Name of the local directory within the CmisSync directory.
        // For instance: "User Homes"
        private string name;
        public string Name { get { return name; } set { name = value; } }

        // Full path to the local database.
        // For instance: C:\\Users\\win7pro32bit\\AppData\\Roaming\\cmissync\\User Homes.cmissync
        private string cmisdatabase;
        public string CmisDatabase { get { return cmisdatabase; } set { cmisdatabase = value; } }

        // Path on the remote server, starting from the root of the CMIS repository.
        // For instance: /User Homes or /Sites/mysite/myfolder
        private string remotepath;
        public string RemotePath { get { return remotepath; } set { remotepath = value; } }

        // Address of the server's CMIS endpoint.
        // For instance: http://192.168.0.1:8080/alfresco/cmisatom
        private Uri address;
        public Uri Address { get { return address; } set { address = value; } }

        // CMIS user
        private string user;
        public string User { get { return user; } set { user = value; } }

        // CMIS password, hashed.
        // For instance: AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAAtiSvUCYn...
        private string password;
        public string Password { get { return password; } set { password = value; } }

        // Identifier of the CMIS repository within the CMIS server.
        // For instance: 7d52d0bd-5108-4dba-8102-48b63b9d3541
        private string repoid;
        public string RepoID { get { return repoid; } set { repoid = value; } }

        // In case the user choose to put the synchronized folder outside of the CmisSync directory, this stores the path to it.
        private string targetdirectory;
        public string TargetDirectory { get { return targetdirectory; } set { targetdirectory = value; } }

        // Poll interval, in milliseconds.
        // CmisSync will sync this remote folder once during this interval of time.
        private double pollinterval;
        public double PollInterval { get { return pollinterval; } set { pollinterval = value; } }

        public RepoInfo(string name, string cmisDatabaseFolder)
        {
            this.name = name;
            this.cmisdatabase = Path.Combine(cmisDatabaseFolder, name + ".cmissync");
        }

        public RepoInfo(string name, string cmisDatabaseFolder, string remotepath, string address, string user, string password, string repoid, double pollinterval)
        {
            this.name = name;
            this.cmisdatabase = Path.Combine(cmisDatabaseFolder, name + ".cmissync");
            this.remotepath = remotepath;
            this.address = new Uri(address);
            this.user = user;
            this.password = password;
            this.repoid = repoid;
            this.targetdirectory = Path.Combine(ConfigManager.CurrentConfig.FoldersPath, name);
            this.pollinterval = pollinterval;
        }
    }
}
