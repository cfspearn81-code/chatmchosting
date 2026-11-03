using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Threading;
using Harbor.Core;
using Harbor.Foundation;
using Harbor.Installers;
using Harbor.Management;

namespace Harbor.App;

public sealed class ActionCommand(Func<Task> action) : ICommand
{
    public bool CanExecute(object? parameter) => true;
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public async void Execute(object? parameter) => await action();
}
public sealed record ServerCard(ServerDefinition Server,string State);
public sealed class MainViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this,new(name));
    readonly ServerRepository repository = new();
    readonly OperationCoordinator coordinator = new();
    public ServerLifecycleManager Lifecycle { get; }
    readonly ServerInstaller installer;
    readonly ModService mods;
    readonly BackupService backups;
    readonly PropertiesService properties;
    readonly ServerService administration;
    readonly WorldService worlds;
    readonly PlayerService players;
    CancellationTokenSource? operation;
    public ObservableCollection<ServerDefinition> Servers { get; } = [];
    public ObservableCollection<string> Releases { get; } = [];
    public ObservableCollection<string> Logs { get; } = [];
    public ObservableCollection<string> ModEntries { get; } = [];
    public ObservableCollection<BackupInfo> Backups { get; } = [];
    public ServerLoader[] Loaders { get; } = Enum.GetValues<ServerLoader>();
    ServerDefinition? selected;
    public ServerDefinition? Selected { get => selected; set { selected=value; Changed(); Changed(nameof(HasServer)); Changed(nameof(Title)); Changed(nameof(Details)); Logs.Clear(); RefreshStatus(); _=Run(RefreshDetails); } }
    public bool HasServer => Selected is not null;
    public bool Empty=>Servers.Count==0;
    public IEnumerable<ServerCard> Cards=>Servers.Select(x=>new ServerCard(x,Lifecycle.GetStatus(x.Id).State.ToString()));
    public string Title => Selected?.Name ?? "Your servers";
    public string Details => Selected is {} s ? $"Minecraft {s.MinecraftVersion} · {s.Loader} · {s.MaximumMemoryMb:N0} MiB heap · port {s.Port}" : "A local home for your Minecraft worlds.";
    string status = "Ready to create your first server.";
    public string Status { get=>status; set {status=value;Changed();} }
    string serverStatus="No server selected";
    public string ServerStatus { get=>serverStatus;set{serverStatus=value;Changed();} }
    string metrics="Metrics available while a server is running.";
    public string Metrics {get=>metrics;set{metrics=value;Changed();}}
    bool busy;
    public bool Busy {get=>busy;set{busy=value;Changed();Changed(nameof(NotBusy));}}
    public bool NotBusy=>!Busy;
    bool create;
    public bool ShowCreate {get=>create;set{create=value;Changed();}}
    public string NewName {get;set;}="My Minecraft server";
    public string? NewVersion {get;set;}
    public ServerLoader NewLoader {get;set;}
    public string LoaderVersion {get;set;}="";
    public string JavaPath {get;set;}="java";
    public string DataDirectory {get;set;}=HarborPaths.DataDirectory;
    public decimal? Port {get;set;}=25565;
    public decimal? Memory {get;set;}=2048;
    public bool AcceptEula {get;set;}
    public string Command {get;set;}="";
    string search="";
    public string Search {get=>search;set{search=value;Changed();Changed(nameof(VisibleLogs));}}
    public IEnumerable<string> VisibleLogs=>Logs.Where(x=>string.IsNullOrEmpty(Search)||x.Contains(Search,StringComparison.OrdinalIgnoreCase));
    public ObservableCollection<string> CommandHistory {get;}=[];
    public decimal? JavaMajor {get;set;}=21;
    public ObservableCollection<string> LoaderVersions {get;}=[];
    public ActionCommand LoaderCatalogCommand {get;}
    public ActionCommand RetryCatalogCommand {get;}
    public ActionCommand JavaDownloadCommand {get;}

    public string PropertiesText {get;set;}="";
    public string? SelectedMod {get;set;}
    public BackupInfo? SelectedBackup {get;set;}
    public ActionCommand CreateCommand {get;}
    public ActionCommand OpenCreateCommand {get;}
    public ActionCommand StartCommand {get;}
    public ActionCommand StopCommand {get;}
    public ActionCommand RestartCommand {get;}
    public ActionCommand SendCommand {get;}
    public ActionCommand BackupCommand {get;}
    public ActionCommand SavePropertiesCommand {get;}
    public ActionCommand CancelCommand {get;}
    public ActionCommand DiscoverJavaCommand {get;}
    public MainViewModel()
    {
        Lifecycle=new(coordinator); installer=new(new VerifiedDownloader(),coordinator);
        players=new(coordinator,Lifecycle);administration=new(repository,coordinator,Lifecycle);worlds=new(coordinator,Lifecycle);mods=new(coordinator,Lifecycle); backups=new(coordinator,Lifecycle); properties=new(coordinator,Lifecycle);
        OpenCreateCommand=new(()=> {ShowCreate=!ShowCreate;return Task.CompletedTask;});
        CreateCommand=new(()=>Run(CreateAsync));
        StartCommand=new(()=>Run(async()=>{if(Selected is {} s) {UpdateMemoryWarning();await Lifecycle.StartAsync(s,ServerInstaller.CreateLaunchPlan(s));}}));
        RestartCommand=new(()=>Run(async()=>{if(Selected is {} s){UpdateMemoryWarning();await Lifecycle.StopAsync(s.Id);await Lifecycle.StartAsync(s,ServerInstaller.CreateLaunchPlan(s));}}));
        StopCommand=new(()=>Run(async()=>{if(Selected is {} s) await Lifecycle.StopAsync(s.Id);}));
        SendCommand=new(()=>Run(async()=>{if(Selected is {} s) {await Lifecycle.SendCommandAsync(s.Id,Command);CommandHistory.Insert(0,Command);while(CommandHistory.Count>50)CommandHistory.RemoveAt(50);Command="";Changed(nameof(Command));}}));
        BackupCommand=new(()=>Run(async()=>{if(Selected is {} s){await backups.CreateAsync(s);await RefreshDetails();Status="Full server backup created.";}}));
        SavePropertiesCommand=new(()=>Run(async()=>{if(Selected is {} s){var changes=JavaProperties.Parse(PropertiesText).Values;await properties.SaveAsync(s,changes);Status="Properties saved. Changes apply on the next start.";}}));
        CancelCommand=new(()=>{operation?.Cancel();return Task.CompletedTask;});
        DiscoverJavaCommand=new(()=>Run(async()=>{var found=await new JavaDiscovery().DiscoverAsync();if(found.Count==0) throw new InvalidOperationException("No compatible Java found. Install Java 17 or 21, or enter a custom executable.");JavaPath=found.OrderByDescending(x=>x.MajorVersion).First().Executable;Changed(nameof(JavaPath));Status=$"Found {found.Count} Java runtime(s). Selected {JavaPath}.";}));
        RetryCatalogCommand=new(()=>Run(async()=>{Releases.Clear();foreach(var r in await installer.GetReleasesAsync(operation!.Token))Releases.Add(r.Id);NewVersion=Releases.FirstOrDefault();Changed(nameof(NewVersion));Status="Minecraft release catalog loaded.";}));
        LoaderCatalogCommand=new(()=>Run(async()=>{if(NewVersion is null)throw new InvalidOperationException("Choose a Minecraft release.");LoaderVersions.Clear();foreach(var v in await installer.GetLoaderVersionsAsync(NewLoader,NewVersion,operation!.Token))LoaderVersions.Add(v);Status=$"Loaded {LoaderVersions.Count} published loader versions.";}));
        JavaDownloadCommand=new(()=>Run(async()=>{var service=new JavaRuntimeInstaller(new VerifiedDownloader());var offer=await service.ResolveAsync((int)(JavaMajor??21),operation!.Token);Status=$"Installing {offer.Name} from Eclipse Temurin. {offer.License}.";var java=await service.InstallAsync(offer,Path.Combine(HarborPaths.DataDirectory,"runtimes"),cancellationToken:operation.Token);JavaPath=java.Executable;Changed(nameof(JavaPath));Status="Verified managed Java installed. System Java has not changed.";}));
        Lifecycle.OutputReceived+=(_,line)=>Dispatcher.UIThread.Post(()=>{if(Selected?.Id==line.ServerId){Logs.Add($"{line.Timestamp:HH:mm:ss}  {line.Text}");while(Logs.Count>1500)Logs.RemoveAt(0);Changed(nameof(VisibleLogs));}});
        Lifecycle.StatusChanged+=(_,s)=>Dispatcher.UIThread.Post(()=>{RefreshStatus();if(s.Error is not null) Status=s.Error;});
        var timer=new DispatcherTimer {Interval=TimeSpan.FromSeconds(2)};timer.Tick+=(_,_)=>RefreshStatus();timer.Start();
    }
    public Task InitializeAsync()=>Run(async()=>{foreach(var s in await repository.ListAsync()) Servers.Add(s);Selected=Servers.FirstOrDefault();if(Selected is not null)await RefreshDetails();try{foreach(var r in await installer.GetReleasesAsync()) Releases.Add(r.Id);NewVersion=Releases.FirstOrDefault();Changed(nameof(NewVersion));Status="Ready. Minecraft files download only when you create a server.";}catch(Exception e){Status="Version catalog unavailable: "+e.Message;}});
    public async Task Run(Func<Task> action)
    {
        if(Busy){Status="Another operation is in progress. Wait for it to finish or cancel it before continuing.";return;}Busy=true;operation=new();try{await action();}catch(OperationCanceledException){Status="Operation cancelled. Retry when ready.";}catch(Exception e){Status=e.Message;}finally{operation.Dispose();operation=null;Busy=false;}
    }
    async Task CreateAsync()
    {
        if(string.IsNullOrWhiteSpace(NewVersion))throw new InvalidOperationException("Choose a Minecraft release first.");
        if(!AcceptEula)throw new InvalidOperationException("Read and explicitly accept the Minecraft EULA before creating a server.");
        var java=await new JavaDiscovery().ProbeAsync(JavaPath,operation!.Token);
        var id=Guid.NewGuid();
        var server=new ServerDefinition {Id=id,Name=NewName,Directory=Path.Combine(Path.GetFullPath(DataDirectory),"servers",id.ToString("N")),MinecraftVersion=NewVersion,Loader=NewLoader,LoaderVersion=string.IsNullOrWhiteSpace(LoaderVersion)?null:LoaderVersion,JavaPath=JavaPath,JavaMajor=java.MajorVersion,Port=(int)(Port??25565),MaximumMemoryMb=(int)(Memory??2048),EulaAccepted=true,EulaAcceptedAt=DateTimeOffset.UtcNow};
        UpdateMemoryWarning(server.MaximumMemoryMb);
        Status="Installing verified server artifacts…";
        var result=await installer.InstallAsync(new(server,server.LoaderVersion),new Progress<DownloadProgress>(p=>Status=p.TotalBytes is {} total?$"Downloading {p.BytesReceived/1048576d:F1} / {total/1048576d:F1} MiB":"Downloading server artifacts…"),operation.Token);
        await administration.SaveAsync(result.Server);Servers.Add(result.Server);Selected=result.Server;ShowCreate=false;await RefreshDetails();Changed(nameof(Empty));Changed(nameof(Cards));Status="Server installed. Start it when you are ready.";
    }
    public async Task RefreshDetails()
    {
        if(Selected is not {} s)return;
        ModEntries.Clear();foreach(var m in await mods.ListAsync(s))ModEntries.Add(m.Name);
        Backups.Clear();foreach(var b in await backups.ListAsync(s))Backups.Add(b);
        var p=await properties.ReadAsync(s);PropertiesText=p.ToString();Changed(nameof(PropertiesText));
    }
    string playersText="No current player information. Refresh while the server is ready.";
    public string PlayersText {get=>playersText;set{playersText=value;Changed();}}
    public string PlayerName {get;set;}="";
    public Task RefreshPlayersAsync()=>Run(async()=>{if(Selected is {} s){var list=await players.GetConnectedAsync(s);PlayersText=list.Available?$"Observed {list.ObservedAt:HH:mm:ss}: {list.Names.Count} connected player(s)\n{string.Join(", ",list.Names)}":list.Explanation??"Unavailable";}});
    public Task WhitelistAsync(bool add)=>Run(async()=>{if(Selected is {} s)await players.WhitelistCommandAsync(s,PlayerName,add);});
    public Task OperatorAsync(bool grant)=>Run(async()=>{if(Selected is {} s)await players.OperatorCommandAsync(s,PlayerName,grant,true);});
    public Task KickAsync()=>Run(async()=>{if(Selected is {} s)await players.KickAsync(s,PlayerName,true);});
    public Task UpdateServerAsync(ServerDefinition server)=>Run(async()=>{await administration.SaveAsync(server);var old=Servers.First(x=>x.Id==server.Id);Servers[Servers.IndexOf(old)]=server;Selected=server;Status="Server settings saved.";});
    public Task DuplicateAsync(string name,int port)=>Run(async()=>{if(Selected is {} s){var clone=await administration.DuplicateAsync(s,name,Path.Combine(HarborPaths.DataDirectory,"servers",Guid.NewGuid().ToString("N")),port);Servers.Add(clone);Selected=clone;Status="Server duplicated. Review settings and accept the EULA before first start.";}});
    public Task DeleteAsync(bool deleteData)=>Run(async()=>{if(Selected is {} s){await administration.DeleteAsync(s,deleteData,true);Servers.Remove(s);Selected=null;Status=deleteData?"Server and data deleted.":"Server removed. Data has been retained.";}});
    public Task ImportWorldAsync(string path)=>Run(async()=>{if(Selected is {} s){await worlds.ImportAsync(s,path,true,operation!.Token);Status="Java world imported. A recovery backup preserves the previous world.";}});
    public Task ExportWorldAsync(string path)=>Run(async()=>{if(Selected is {} s){await worlds.ExportAsync(s,path,operation!.Token);Status="Java world exported.";}});
    public Task ImportModsAsync(IEnumerable<string> paths)=>Run(async()=>{if(Selected is {} s){await mods.ImportAsync(s,paths);await RefreshDetails();Status="Mods imported. Check loader and client compatibility before starting.";}});
    public Task SetModEnabledAsync(bool enabled)=>Run(async()=>{if(Selected is {} s && SelectedMod is {} m){await mods.SetEnabledAsync(s,m,enabled);await RefreshDetails();}});
    public Task RestoreBackupAsync()=>Run(async()=>{if(Selected is {} s && SelectedBackup is {} b){await backups.RestoreAsync(s,b.Path,true);await RefreshDetails();Status="Backup restored.";}});
    string memoryWarning="";
    public string MemoryWarning {get=>memoryWarning;set{memoryWarning=value;Changed();}}
    public string LanAddresses
    {
        get
        {
            try {var addresses=NetworkingService.GetLanAddresses();return addresses.Count==0?"No usable network adapter address detected. Localhost is still available.":string.Join('\n',addresses.Select(x=>$"{x.Interface}: {x.Address}{(x.IsVpnLike?" (VPN/tunnel — reachability depends on routing)":"")}"));}
            catch(Exception e){return "Network addresses unavailable: "+e.Message;}
        }
    }
    void UpdateMemoryWarning(int additionalMb=0)
    {
        try
        {
            var info=File.ReadLines("/proc/meminfo").Where(x=>x.StartsWith("MemTotal:")||x.StartsWith("MemAvailable:")).Select(x=>x.Split(' ',StringSplitOptions.RemoveEmptyEntries)).ToDictionary(x=>x[0].TrimEnd(':'),x=>long.Parse(x[1])/1024);
            var configured=Servers.Sum(x=>(long)x.MaximumMemoryMb)+additionalMb;
            var available=info.GetValueOrDefault("MemAvailable");var total=info.GetValueOrDefault("MemTotal");
            const long reserve=2048;
            MemoryWarning=configured+reserve>total||configured>available?$"Memory warning: all configured heaps total {configured:N0} MiB; {available:N0} MiB currently available of {total:N0} MiB. Leave at least {reserve:N0} MiB for Kubuntu, plus room for Minecraft. Stopped servers use no Java heap; running all allocations may exceed capacity.":$"All configured heaps: {configured:N0} MiB · currently available RAM: {available:N0} MiB. Leave additional room for Kubuntu and the Minecraft client.";
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or FormatException){MemoryWarning="Available RAM could not be measured. Review total heaps and leave room for Kubuntu and the client.";}
    }
    void RefreshStatus(){UpdateMemoryWarning();Changed(nameof(LanAddresses));Changed(nameof(Cards));Changed(nameof(Empty));ServerStatus=Selected is {} s?Lifecycle.GetStatus(s.Id).State.ToString():"No server selected";if(Selected is {} a && Lifecycle.GetMetrics(a.Id) is {} m) Metrics=$"Uptime {m.Uptime:hh\\:mm\\:ss} · resident {m.ResidentMemoryBytes/1048576d:F0} MiB · CPU {(m.CpuPercent is {} c?$"{c:F1}%":"unavailable")}";else Metrics="Metrics available while a server is running.";}
}
