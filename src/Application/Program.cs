using System;
using System.Collections;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Web.Administration;
using NLog;
using NLog.Config;
using NLog.Targets;
using Newtonsoft.Json;

namespace IisInstaller
{
    class Program
    {
        private static ServerManager _serverManager;
        private static JsonSerializer _jsonSerializer;
        private static Logger _logger;

        static void Main(string[] args)
        {
            try
            {
                InitLogger();

                _jsonSerializer = new JsonSerializer();
                _serverManager = new ServerManager();

                _logger.Info("Installation started...");

                string sitesJsonPath = ConfigurationManager.AppSettings["sites-config"];
                var sites = GetSites(sitesJsonPath);

                if (sites == null)
                {
                    throw new Exception("No installation data found, exiting.");
                }

                _logger.Info("{0} sites found", sites.Count());
                for (var i = 0; i < sites.Count(); i++)
                {
                    var site = sites[i];

                    // Create Site
                    _logger.Info("Creating Site...");
                    CreateSite(site);

                    // Commit changes
                    _logger.Info("Committing changes...");
                    _serverManager.CommitChanges();
                }

                _logger.Info("Installation complete!");
            }
            catch (Exception ex)
            {
                _logger.Error("Installation failed: ");
                _logger.Error(ex);
            }
            finally
            {
                Console.ReadKey();
            }
        }

        private static IList<IisSite> GetSites(string filePath)
        {
            _logger.Info("Locating installation data from {0}...", filePath);
            if (File.Exists(filePath))
            {
                return JsonConvert.DeserializeObject<List<IisSite>>(File.ReadAllText(filePath));
            }

            _logger.Fatal("{0} not found", filePath);
            return null;
        }

        static Application CreateApp(Site createdSite, IisSite site, IisSiteApplication app)
        {
            _logger.Info("Creating application {0}...", app.Name);

            if (app.AppPoolName == null)
            {
                app.AppPoolName = site.Name;
            }

            var appPool = _serverManager.ApplicationPools.SingleOrDefault(x => x.Name == app.AppPoolName);
            if (appPool == null)
            {
                _logger.Info("App pool {0} not found, creating...", app.AppPoolName);
                CreateAppPool(new IisApplicationPool
                {
                    Name = app.AppPoolName,
                    ServiceAccount = app.ServiceAccount,
                    ManagedRuntimeVersion = site.ManagedRuntimeVersion
                });
            }

            if (app.ServiceAccount == null)
            {
                app.ServiceAccount = site.ServiceAccount;
            }

            Application createdApp = createdSite.Applications.Add(app.Path, app.PhysicalPath);
            createdApp.VirtualDirectories[0].UserName = app.ServiceAccount.Username;
            createdApp.VirtualDirectories[0].Password = app.ServiceAccount.Password;
            createdApp.ApplicationPoolName = app.AppPoolName;
            
            if (app.VirtualDirectories != null)
            {
                _logger.Info(string.Format("Creating child application virtual directories..."));
                foreach (IisSiteVirtualDirectory vd in app.VirtualDirectories)
                {
                    if (vd.ServiceAccount == null)
                    {
                        vd.ServiceAccount = app.ServiceAccount;
                    }

                    CreateVirtualDirectory(createdSite, createdApp, site, vd);
                }
            }
            _logger.Info("Application {0} created successfully!", app.Name);
            return createdApp;
        }

        static ApplicationPool CreateAppPool(IisApplicationPool pool)
        {
            _logger.Info("Creating application pool {0}...", pool.Name);            

            var appPool = _serverManager.ApplicationPools.Add(pool.Name);
            appPool.ProcessModel.UserName = pool.ServiceAccount.Username;
            appPool.ProcessModel.Password = pool.ServiceAccount.Password;
            appPool.ManagedRuntimeVersion = pool.ManagedRuntimeVersion;
            _logger.Info("App pool {0} created successfully!", pool.Name);

            return appPool;
        }

        static VirtualDirectory CreateVirtualDirectory(Site createdSite, Application createdApp, IisSite site, IisSiteVirtualDirectory virtualDir)
        {
            _logger.Info("Creating virtual directory {0}...", virtualDir.Name);

            // If certain properties were not specified, use the parent's
            if (virtualDir.ServiceAccount == null)
            {
                virtualDir.ServiceAccount = site.ServiceAccount;
            }     

            VirtualDirectory newVirtualDir = createdApp.VirtualDirectories.Add(virtualDir.Path, virtualDir.PhysicalPath);
            newVirtualDir.UserName = virtualDir.ServiceAccount.Username;
            newVirtualDir.Password = virtualDir.ServiceAccount.Password;

            _logger.Info(string.Format("Creating child applications..."));
            if (virtualDir.Applications != null)
            {
                foreach (IisSiteApplication childApp in virtualDir.Applications)
                {
                    if (childApp.ServiceAccount == null)
                    {
                        childApp.ServiceAccount = virtualDir.ServiceAccount;
                    }

                    CreateApp(createdSite, site, childApp);
                }
            }

            _logger.Info("Virtual directory {0} created successfully!", virtualDir.Name);
            return newVirtualDir;
        }

        static Site CreateSite(IisSite site)
        {
            Site createdSite;

            try
            {
                var existingSite = _serverManager.Sites.SingleOrDefault(x => x.Name == site.Name);

                _logger.Info(string.Format("Creating site {0}...", site.Name));

                if (existingSite != null)
                {
                    _logger.Info(string.Format("Site already exists"));

                    if (site.Options.DeleteExistingSite)
                    {
                        _logger.Info("Deleting existing site...");
                        _serverManager.Sites.Remove(existingSite);
                    }
                    else
                    {
                        throw new Exception("IisSite.Options.DeleteExistingSite is FALSE, exiting.");
                    }
                }

                if (site.Options.CreatePhysicalDirectory)
                {
                    _logger.Info("Creating physical directory {0}...", site.PhysicalPath);
                    Directory.CreateDirectory(site.PhysicalPath);
                }
                else
                {
                    if (!Directory.Exists(site.PhysicalPath))
                    {
                        _logger.Info("Warning, physical directory {0} does not exist.", site.PhysicalPath);
                    }
                }

                _logger.Info(string.Format("Adding site to IIS with initial binding {0}:{1}...", site.DefaultBinding.Protocol, site.DefaultBinding.BindingInformation));

                if (site.AppPoolName == null)
                {
                    site.AppPoolName = site.Name;
                    _logger.Info("No application pool name specificed, using site name {0}", site.AppPoolName);
                                      
                }

                var appPool = _serverManager.ApplicationPools.SingleOrDefault(x => x.Name == site.AppPoolName);
                if (appPool == null)
                {
                    _logger.Info("App pool {0} not found, creating...", site.AppPoolName);
                    CreateAppPool(new IisApplicationPool
                    {
                        Name = site.AppPoolName,
                        ServiceAccount = site.ServiceAccount,
                        ManagedRuntimeVersion = site.ManagedRuntimeVersion
                    });
                }

                // Creating a site requires an initial binding
                createdSite = _serverManager.Sites.Add(site.Name, site.DefaultBinding.Protocol, site.DefaultBinding.BindingInformation, site.PhysicalPath);

                var rootApp = createdSite.Applications.Single(x => x.Path == "/");
                rootApp.VirtualDirectories[0].UserName = site.ServiceAccount.Username;
                rootApp.VirtualDirectories[0].Password = site.ServiceAccount.Password;
                rootApp.ApplicationPoolName = site.AppPoolName;

                // Add the rest of the bindings
                foreach (IisSiteBinding binding in site.Bindings)
                {
                    _logger.Info(string.Format("Adding additional binding {0}:{1}...", binding.Protocol, binding.BindingInformation));
                    createdSite.Bindings.Add(binding.BindingInformation, binding.Protocol);
                }

                _logger.Info(string.Format("Configuring additional site options..."));

                // Set auto start property
                createdSite.ServerAutoStart = site.ServerAutoStart.HasValue ? site.ServerAutoStart.Value : true;

                _logger.Info(string.Format("Creating root Virtual Directories..."));
                
                foreach (IisSiteVirtualDirectory vd in site.VirtualDirectories)
                {             
                    CreateVirtualDirectory(createdSite, rootApp, site, vd);
                }

                _logger.Info(string.Format("Creating child applications..."));
                foreach (IisSiteApplication app in site.Applications)
                {
                    CreateApp(createdSite, site, app);
                }

                _logger.Info(string.Format("Site created successfully!"));

                return createdSite;
            }
            catch (Exception ex)
            {
                _logger.Fatal(string.Format("Site creation failed! {0}", ex.Message));
                throw;
            }
        }

        static void InitLogger()
        {
            var config = new LoggingConfiguration();

            var consoleTarget = new ColoredConsoleTarget();
            config.AddTarget("console", consoleTarget);

            var fileTarget = new FileTarget();
            config.AddTarget("file", fileTarget);

            consoleTarget.Layout = @"${message}";
            fileTarget.FileName = "log.txt";
            fileTarget.Layout = @"[${date:format=HH\:MM\:ss}]: ${message}";

            config.LoggingRules.Add(new LoggingRule("*", LogLevel.Debug, consoleTarget));
            config.LoggingRules.Add(new LoggingRule("*", LogLevel.Debug, fileTarget));

            LogManager.Configuration = config;
            _logger = LogManager.GetLogger("Main");
        }
    }

    public class IisSite
    {
        public string Name { get; set; }
        public string PhysicalPath { get; set; }
        public string AppPoolName { get; set; }
        public string ManagedRuntimeVersion { get; set; }
        public IisSiteBinding DefaultBinding { get; set; }
        public IList<IisSiteBinding> Bindings { get; set; }
        public IList<IisSiteApplication> Applications { get; set; }
        public IList<IisSiteVirtualDirectory> VirtualDirectories { get; set; }
        public ServiceAccount ServiceAccount { get; set; }
        public InstallationOptions Options { get; set; }
        public bool? ServerAutoStart { get; set; }
    }

    public class IisSiteBinding
    {
        public string Protocol { get; set; }
        public string IpAddress { get; set; }
        public string Hostname { get; set; }
        public int Port { get; set; }
        public string CertificateStoreName { get; set; }

        public string BindingInformation
        {
            get
            {
                return string.Format("{0}:{1}:{2}", this.IpAddress, this.Port, this.Hostname);
            }
        }
    }

    public class IisApplicationPool
    {
        public string Name { get; set; }
        public ServiceAccount ServiceAccount { get; set; }
        public string ManagedRuntimeVersion { get; set; }
    }

    public class IisSiteApplication
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string PhysicalPath { get; set; }
        public ServiceAccount ServiceAccount { get; set; }
        public string AppPoolName { get; set; }
        public IEnumerable<IisSiteVirtualDirectory> VirtualDirectories { get; set; }
    }

    public class IisSiteVirtualDirectory
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string PhysicalPath { get; set; }
        public ServiceAccount ServiceAccount { get; set; }
        public IEnumerable<IisSiteApplication> Applications { get; set; }
    }

    public class ServiceAccount
    {
        public string Username { get; set; }
        public string Password { get; set; }
    }

    public class InstallationOptions
    {
        public bool CreatePhysicalDirectory { get; set; }
        public bool DeleteExistingSite { get; set; }
        public bool DeleteExistingAppPools { get; set; }
    }
}
