﻿using System;
using System.Security.Principal;
using System.DirectoryServices;
using System.DirectoryServices.ActiveDirectory;
using System.DirectoryServices.Protocols;
using ADCollector2;
using SearchScope = System.DirectoryServices.Protocols.SearchScope;
using SearchOption = System.DirectoryServices.Protocols.SearchOption;
using System.Collections.Generic;
using IniParser;
using IniParser.Model;
using System.Security.AccessControl;
using System.Xml;
using System.IO;
using System.Linq;

namespace ADCollector2
{
    internal static class Functions
    {


        public static LdapConnection GetConnection(string domainName, bool ldaps)
        {
            var port = ldaps ? 636 : 389;

            LdapDirectoryIdentifier identifier = new LdapDirectoryIdentifier(domainName, port);

            var conn = new LdapConnection(identifier)
            {
                Timeout = new TimeSpan(0, 0, 5, 0)
            };

            if (ldaps)
            {
                conn.SessionOptions.ProtocolVersion = 3;
                conn.SessionOptions.SecureSocketLayer = true;
            }
            //Console.WriteLine("\n * LDAP Server is Connected");
            return conn;
        }



        public static void GetResponse(LdapConnection conn,
                                        string filter,
                                        SearchScope scope,
                                        string[] attrsToReturn,
                                        string dn,
                                        string printOption = null,
                                        string spnName = null)
                                        //Dictionary<string, string> myNames = null)
        {

            var request = new SearchRequest(dn, filter, scope, attrsToReturn);

            // the size of each page
            var pageReqControl = new PageResultRequestControl(500);

            // turn off referral chasing so that data 
            // from other partitions is not returned

            //var searchControl = new SearchOptionsControl(SearchOption.DomainScope);
            //Unhandled Exception: System.ComponentModel.InvalidEnumArgumentException: 
            //The value of argument 'value' (0) is invalid for Enum type 'SearchOption'.
            var searchControl = new SearchOptionsControl();

            request.Controls.Add(pageReqControl);
            request.Controls.Add(searchControl);


            SearchResponse response;
            PageResultResponseControl pageResControl;

            // loop through each page
            while (true)
            {
                try
                {
                    response = (SearchResponse)conn.SendRequest(request);

                    if (response.Controls.Length != 1 || !(response.Controls[0] is PageResultResponseControl))
                    {
                        Console.WriteLine("The server does not support this advanced search operation");
                        return;
                    }
                    pageResControl = (PageResultResponseControl)response.Controls[0];

                    //Console.WriteLine("\nThis page contains {0} response entries:\n", response.Entries.Count);

                    switch (printOption)
                    {
                        //if there's only one attribute needs to be returned
                        //and this attribute is a single-valued attribute
                        case "single":
                            Outputs.PrintSingle(response, attrsToReturn[0]);
                            break;

                        //if there's only one attribute needs to be returned
                        //and this attribute is a multi-valued attribute
                        case "multi":
                            Outputs.PrintMulti(response, attrsToReturn[0]);
                            break;

                        ////Use specified name paris
                        //case "mynames":
                            //Outputs.PrintMyName(response, myNames);
                            //break;

                        case "gpo":
                            Outputs.PrintGPO(response);
                            break;

                        case "spn":
                            Outputs.PrintSPNs(response, spnName);
                            break;

                        case "domain":
                            Outputs.PrintDomainAttrs(response);
                            break;

                        //case "attrname":
                            //Outputs.PrintAttrName(response);
                            //break;

                        //default: print all attributesToReturned
                        default:
                            Outputs.PrintAll(response);
                            break;
                    }


                    if (pageResControl.Cookie.Length == 0) { break; }

                    pageReqControl.Cookie = pageResControl.Cookie;
                }
                catch (Exception e)
                {
                    Console.WriteLine("Unexpected error:  {0}", e.Message);
                    break;
                }
            }
        }


        public static void GetDomains(Forest currentForest)
        {
            foreach (Domain domain in currentForest.Domains)
            {
                try
                {
                    Console.WriteLine("  * {0}", domain.Name);

                    DirectoryEntry domainEntry = domain.GetDirectoryEntry();

                    using (domainEntry)
                    {

                        var domainSID = new SecurityIdentifier((byte[])domainEntry.Properties["objectSid"][0], 0);

                        Console.WriteLine("    Domain SID:   {0}\n", domainSID);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.GetType().Name + " : " + e.Message);
                }

            }
        }



        public static void GetDCs(Domain currentDomain)
        {
            //foreach (DomainController dc in currentDomain.FindAllDomainControllers())
            foreach (DomainController dc in currentDomain.FindAllDiscoverableDomainControllers())
            {
                try
                {
                    string DCType = "";

                    if (dc.IsGlobalCatalog())
                    {
                        DCType += "[Global Catalog] ";
                    }

                    using (DirectoryEntry dcServerEntry = dc.GetDirectoryEntry())
                    {
                        //Console.WriteLine(dcEntry.Properties["primaryGroupID"].Value);
                        //Exception: Object reference not set to an instance of an object

                        //https://stackoverflow.com/questions/34925136/why-property-primarygroupid-missing-for-domain-controller
                        //dc.GetDirectoryEntry() returns a server object, not the computer object of DC
                        //primaryGroupID does not exist on server object

                        using (DirectoryEntry dcEntry = new DirectoryEntry("LDAP://" + dcServerEntry.Properties["serverReference"].Value))
                        {
                            //Check if the primaryGroupID attribute has a value of 521 (Read-Only Domain Controller)
                            if ((int)dcEntry.Properties["primaryGroupID"].Value == 521)
                            {
                                DCType += "[Read-Only Domain Controller]";
                            }
                        }
                    }

                    Console.WriteLine("  * {0}  {1}", dc.Name, DCType);
                    Console.WriteLine("    IPAddress        :  {0}", dc.IPAddress);
                    Console.WriteLine("    OS               :  {0}", dc.OSVersion);
                    Console.WriteLine("    Site             :  {0}", dc.SiteName);

                    string roles = "";

                    foreach (var role in dc.Roles)
                    {
                        roles += role + "   ";
                    }
                    if(roles != "")
                    {
                        Console.WriteLine("    Roles            :  {0}", roles);
                    }

                    Console.WriteLine();

                }
                catch (Exception)
                {
                    Console.WriteLine();
                    Console.WriteLine("  * {0}:  RPC server is unavailable.", dc.Name);
                    Console.WriteLine();
                }
            }
        }




        public static void GetDomainTrusts(Domain currentDomain)
        {
            string sidStatus;

            if (currentDomain.GetAllTrustRelationships().Count > 0)
            {
                Console.WriteLine("    {0,-30}{1,-30}{2,-15}{3,-20}\n", "Source", "Target", "TrustType", "TrustDirection");
            }

            foreach (TrustRelationshipInformation trustInfo in currentDomain.GetAllTrustRelationships())
            {
                if (currentDomain.GetSidFilteringStatus(trustInfo.TargetName))
                {
                    sidStatus = "[SID Filtering is enabled]\n";
                }
                else
                {
                    sidStatus = "[Not Filtering SIDs]\n";
                }

                Console.WriteLine("    {0,-30}{1,-30}{2,-15}{3,-20}{4,-10}", trustInfo.SourceName, trustInfo.TargetName, trustInfo.TrustType, trustInfo.TrustDirection, sidStatus);
            }
        }



        public static void GetForestTrusts(Forest currentForest)
        {
            string sidStatus = "";

            if (currentForest.GetAllTrustRelationships().Count > 0)
            {
                Console.WriteLine("    {0,-30}{1,-30}{2,-15}{3,-20}\n", "Source", "Target", "TrustType", "TrustDirection");
            }

            foreach (TrustRelationshipInformation trustInfo in currentForest.GetAllTrustRelationships())
            {
                try
                {
                    if (currentForest.GetSidFilteringStatus(trustInfo.TargetName))
                    {
                        sidStatus = "[SID Filtering is enabled]\n";
                    }
                    else
                    {
                        sidStatus = "[Not Filtering SIDs]\n";
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine("Something wrong with SID filtering");

                    Console.WriteLine("Error: {0}\n", e.Message);
                }

                Console.WriteLine("    {0,-30}{1,-30}{2,-15}{3,-20}{4,-10}", trustInfo.SourceName, trustInfo.TargetName, trustInfo.TrustType, trustInfo.TrustDirection, sidStatus);
            }
        }


        //Kerberos policy
        //reference: https://docs.microsoft.com/en-us/openspecs/windows_protocols/ms-gpsb/0fce5b92-bcc1-4b96-9c2b-56397c3f144f

        public static void GetDomainPolicy(string domainName)
        {
            var parser = new FileIniDataParser();
            try
            {
                string gptPath = "\\\\" + domainName + "\\SYSVOL\\" + domainName + "\\Policies\\{31B2F340-016D-11D2-945F-00C04FB984F9}\\MACHINE\\Microsoft\\Windows NT\\SecEdit\\GptTmpl.inf";
                IniData data = parser.ReadFile(gptPath);

                Console.WriteLine("    MaxServiceAge:           {0} Minutes", data["Kerberos Policy"]["MaxServiceAge"]);
                Console.WriteLine("    MaxTicketAge:            {0} Hours", data["Kerberos Policy"]["MaxTicketAge"]);
                Console.WriteLine("    MaxRenewAge:             {0} Days", data["Kerberos Policy"]["MaxRenewAge"]);
                Console.WriteLine("    MaxClockSkew:            {0} Minutes", data["Kerberos Policy"]["MaxClockSkew"]);
                Console.WriteLine("    TicketValidateClient:    {0}", data["Kerberos Policy"]["TicketValidateClient"]);

            }
            catch (Exception e)
            {
                Console.WriteLine("Error: {0}\n", e.Message);
            }
            

        }


        public static void GetInterestingAcls(string targetDn, string forestDn)
        {
            using(var entry = new DirectoryEntry("LDAP://" + targetDn))
            {
                ActiveDirectorySecurity sec = entry.ObjectSecurity;

                AuthorizationRuleCollection rules = null;

                rules = sec.GetAccessRules(true, true, typeof(NTAccount));

                Console.WriteLine("  * Object DN: {0}", targetDn);
                Console.WriteLine();

                foreach (ActiveDirectoryAccessRule rule in rules)
                {
                    Outputs.PrintAce(rule, forestDn);
                }
            }
        }

        public static string ResolveRightsGuids(string forestDn, string rightsGuid)
        {
            var extrightsDn = "CN=Extended-Rights,CN=Configuration," + forestDn;

            var rightsEntry = new DirectoryEntry("LDAP://" + extrightsDn);

            var rightsSearcher = new DirectorySearcher(rightsEntry);

            string rightFilter = @"(rightsGuid=" + rightsGuid + @")";

            rightsSearcher.Filter = rightFilter;
            rightsSearcher.SearchScope = System.DirectoryServices.SearchScope.OneLevel;
            var rightsFinder = rightsSearcher.FindOne();


            var rightDn = rightsFinder.Properties["cn"][0].ToString();

            return rightDn;
            

        }


        //https://social.msdn.microsoft.com/Forums/vstudio/en-US/957c6799-02c2-4a1d-b6ad-c573b80a69d5/continuing-on-error-with-directorygetfiles?forum=csharpgeneral

        public static List<string> GetGPPXml(string fPath)
        {
            var xmlList = new List<string> { "Groups.xml", "Services.xml", "Scheduledtasks.xml", "Datasources.xml", "Printers.xml", "Drives.xml" };

            var files = new List<string>();

            foreach (string file in Directory.GetFiles(fPath))
            {
                try
                {
                    if (xmlList.Any(file.Contains))
                    {
                        files.Add(file);
                    }
                }
                catch { }
            }
            foreach (string directory in Directory.GetDirectories(fPath))
            {
                try
                {
                    GetGPPXml(directory);
                }
                catch { }
            }
            return files;
        }


        //https://github.com/PowerShellMafia/PowerSploit/blob/master/Privesc/PowerUp.ps1

        public static List<string> GetCachedGPP()
        {
            string allUser = Environment.GetEnvironmentVariable("ALLUSERSPROFILE");

            return allUser.Contains("ProgramData") ? GetGPPXml(allUser) : GetGPPXml(allUser + @"\Application Data");

        }



        //https://github.com/PowerShellMafia/PowerSploit/blob/master/Exfiltration/Get-GPPPassword.ps1
        //Search for groups.xml, scheduledtasks.xml, services.xml, datasources.xml, printers.xml and drives.xml
        //findstr /S /I cpassword \\<FQDN>\sysvol\<FQDN>\policies\*.xml
        public static void GetGPPP(List<string> files)
        {

            IDictionary<string, string> gppDict = new Dictionary<string, string>();
            gppDict.Add("Groups.xml", "/Groups/User/Properties");
            gppDict.Add("Services.xml", "/NTServices/NTService/Properties");
            gppDict.Add("Scheduledtasks.xml", "/ScheduledTasks/Task/Properties");
            gppDict.Add("Datasources.xml", "/DataSources/DataSource/Properties");
            gppDict.Add("Printers.xml", "/Printers/SharedPrinter/Properties");
            gppDict.Add("Drives.xml", "/Drives/Drive/Properties");

            XmlDocument doc = new XmlDocument();

            foreach (string path in files)
            {
                try
                {
                    doc.Load(path);
                }
                catch
                {
                    Console.WriteLine("Error loading file {0}", path);
                }


                foreach (KeyValuePair<string, string> gppXml in gppDict)
                {
                    doc.Load(path);

                    if (doc.InnerXml.Contains("cpassword"))
                    {
                        var nodes = doc.DocumentElement.SelectNodes(gppXml.Value);

                        switch (path.Split('\\').Last())
                        {
                            case "Groups.xml":
                                foreach (XmlNode node in nodes)
                                {
                                    try
                                    {
                                        Console.WriteLine("  * userName:    {0}", node.Attributes["userName"].Value);
                                        Console.WriteLine("    newName:     {0}", node.Attributes["newName"].Value);
                                        Console.WriteLine("    cpassword:   {0}", node.Attributes["cpassword"].Value);
                                        Console.WriteLine("    changed:     {0}", node.ParentNode.Attributes["changed"].Value);
                                        Console.WriteLine("    Path:        {0}", path);
                                        
                                    }
                                    catch { }

                                }
                                break;

                            case "Services.xml":
                                foreach (XmlNode node in nodes)
                                {
                                    try
                                    {
                                        Console.WriteLine("  * accountName:     {0}", node.Attributes["accountName"].Value);
                                        Console.WriteLine("    cpassword:       {0}", node.Attributes["cpassword"].Value);
                                        Console.WriteLine("    changed:         {0}", node.ParentNode.Attributes["changed"].Value);
                                        Console.WriteLine("    Path:        {0}", path);
                                    }
                                    catch { }

                                }
                                break;

                            case "Scheduledtasks":
                                foreach (XmlNode node in nodes)
                                {
                                    try
                                    {
                                        Console.WriteLine("  * runAs:       {0}", node.Attributes["runAs"].Value);
                                        Console.WriteLine("    cpassword:   {0}", node.Attributes["cpassword"].Value);
                                        Console.WriteLine("    changed:     {0}", node.ParentNode.Attributes["changed"].Value);
                                        Console.WriteLine("    Path:        {0}", path);
                                    }
                                    catch { }
                                }
                                break;

                            default:
                                foreach (XmlNode node in nodes)
                                {
                                    try
                                    {
                                        Console.WriteLine("  * userName:    {0}", node.Attributes["userName"].Value);
                                        Console.WriteLine("    cpassword:   {0}", node.Attributes["cpassword"].Value);
                                        Console.WriteLine("    changed:     {0}", node.ParentNode.Attributes["changed"].Value);
                                        Console.WriteLine("    Path:        {0}", path);
                                    }
                                    catch { }

                                }
                                break;
                        }
                        
                    }
                    
                }

                Console.WriteLine();
            }

            

            


        }   

    }
}