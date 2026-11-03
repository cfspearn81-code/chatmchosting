using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Harbor.Core;
namespace Harbor.App;
public partial class MainWindow : Window
{
    readonly MainViewModel model = new();
    bool closeAllowed;
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);DataContext=model;
        Opened+=async(_,_)=>await model.InitializeAsync();
        Closing+=OnClosing;
        model.Logs.CollectionChanged+=(_,_)=>{if(this.FindControl<CheckBox>("AutoScroll")?.IsChecked==true && this.FindControl<ListBox>("OutputList") is {} list && model.Logs.Count>0)list.ScrollIntoView(model.Logs[^1]);};
    }
    async void CopyConsole(object? sender,RoutedEventArgs args)
    {
        if(Clipboard is {} clipboard && this.FindControl<ListBox>("OutputList")?.SelectedItems is {} items)
            await clipboard.SetTextAsync(string.Join(Environment.NewLine,items.Cast<string>()));
    }
    void OpenServerCard(object? sender,RoutedEventArgs args){if(sender is Control {DataContext:ServerCard card})model.Selected=card.Server;}
    void StartServerCard(object? sender,RoutedEventArgs args){OpenServerCard(sender,args);model.StartCommand.Execute(null);}
    void StopServerCard(object? sender,RoutedEventArgs args){OpenServerCard(sender,args);model.StopCommand.Execute(null);}
    void ShowDashboard(object? sender,RoutedEventArgs args)=>model.Selected=null;
    async void OpenEula(object? sender,RoutedEventArgs args)
    {
        try {if(Launcher is {} launcher)await launcher.LaunchUriAsync(new("https://www.minecraft.net/eula"));}
        catch(Exception e){model.Status=e.Message;}
    }
    async void RefreshPlayers(object? sender,RoutedEventArgs args)=>await model.RefreshPlayersAsync();
    async void WhitelistAdd(object? sender,RoutedEventArgs args)=>await model.WhitelistAsync(true);
    async void WhitelistRemove(object? sender,RoutedEventArgs args){if(await Confirm("Remove whitelist entry?",$"Remove {model.PlayerName} from the whitelist?","Remove"))await model.WhitelistAsync(false);}
    async void GrantOperator(object? sender,RoutedEventArgs args){if(await Confirm("Grant operator permission?",$"Grant server administration privileges to {model.PlayerName}?","Grant operator"))await model.OperatorAsync(true);}
    async void RevokeOperator(object? sender,RoutedEventArgs args){if(await Confirm("Revoke operator permission?",$"Revoke privileges from {model.PlayerName}?","Revoke operator"))await model.OperatorAsync(false);}
    async void KickPlayer(object? sender,RoutedEventArgs args){if(await Confirm("Kick player?",$"Disconnect {model.PlayerName} from this server?","Kick player"))await model.KickAsync();}
    async void EditServer(object? sender,RoutedEventArgs args)
    {
        if(model.Selected is not {} server)return;
        var name=new TextBox {Text=server.Name};var java=new TextBox {Text=server.JavaPath};var port=new NumericUpDown {Minimum=1024,Maximum=65535,Value=server.Port};var heap=new NumericUpDown {Minimum=512,Maximum=65536,Increment=256,Value=server.MaximumMemoryMb};
        var eula=new CheckBox {Content="I have read and accept the Minecraft EULA",IsChecked=server.EulaAccepted};
        var dialog=new Window {Title="Edit server",Width=500,SizeToContent=SizeToContent.Height,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var save=new Button {Content="Save settings"};save.Classes.Add("primary");save.Click+=(_,_)=>dialog.Close(true);
        dialog.Content=new StackPanel {Margin=new(24),Spacing=12,Children={new TextBlock {Text="Server name"},name,new TextBlock {Text="Java executable"},java,new TextBlock {Text="Port"},port,new TextBlock {Text="Maximum heap (MiB)"},heap,new TextBlock {Text="EULA: https://www.minecraft.net/eula"},eula,save}};
        if(await dialog.ShowDialog<bool>(this))await model.UpdateServerAsync(server with {Name=name.Text??"",JavaPath=java.Text??"",Port=(int)port.Value!,MaximumMemoryMb=(int)heap.Value!,EulaAccepted=eula.IsChecked==true,EulaAcceptedAt=eula.IsChecked==true?server.EulaAcceptedAt??DateTimeOffset.UtcNow:null});
    }
    async void DuplicateServer(object? sender,RoutedEventArgs args)
    {
        if(model.Selected is not {} server)return;
        var port=new NumericUpDown {Minimum=1024,Maximum=65535,Value=server.Port<65535?server.Port+1:25565};
        var name=new TextBox {Text=server.Name+" copy"};var button=new Button {Content="Duplicate server"};button.Classes.Add("primary");
        var dialog=new Window {Title="Duplicate stopped server",Width=440,SizeToContent=SizeToContent.Height,WindowStartupLocation=WindowStartupLocation.CenterOwner};button.Click+=(_,_)=>dialog.Close(true);
        dialog.Content=new StackPanel {Margin=new(24),Spacing=16,Children={new TextBlock {Text="The source must be stopped. The copy gets a new identity, independent files, and requires EULA acceptance.",TextWrapping=Avalonia.Media.TextWrapping.Wrap},name,port,button}};
        if(await dialog.ShowDialog<bool>(this))await model.DuplicateAsync(name.Text??"",(int)port.Value!);
    }
    async void DeleteServer(object? sender,RoutedEventArgs args)
    {
        if(model.Selected is null)return;
        if(await Confirm("Remove this server?","Removes the server from Harbor. All worlds, backups, mods, and other files stay on disk. The server must be stopped.","Remove and retain data"))await model.DeleteAsync(false);
    }
    async void ImportWorld(object? sender,RoutedEventArgs args)
    {
        try
        {
            var paths=await StorageProvider.OpenFolderPickerAsync(new(){Title="Choose a Minecraft Java world directory",AllowMultiple=false});
            if(paths.FirstOrDefault()?.TryGetLocalPath() is {} path && await Confirm("Replace the world?","The selected stopped server’s world will be replaced. A recovery backup is required before replacement.","Import world"))await model.ImportWorldAsync(path);
        }catch(Exception e){model.Status=e.Message;}
    }
    async void ExportWorld(object? sender,RoutedEventArgs args)
    {
        try
        {
            var file=await StorageProvider.SaveFilePickerAsync(new(){Title="Export Minecraft Java world",SuggestedFileName="world.zip",DefaultExtension="zip"});
            if(file?.TryGetLocalPath() is {} path)await model.ExportWorldAsync(path);
        }catch(Exception e){model.Status=e.Message;}
    }
    async void DownloadJava(object? sender,RoutedEventArgs args)
    {
        if(await Confirm("Install managed Java?","Download Linux x64 Eclipse Temurin from Adoptium. License: GPLv2 with Classpath Exception. The published SHA-256 is verified. This changes no system Java settings.","Download runtime"))model.JavaDownloadCommand.Execute(null);
    }
    async void ImportMods(object? sender,RoutedEventArgs args)
    {
        try
        {
            var files=await StorageProvider.OpenFilePickerAsync(new(){Title="Import Minecraft mod JARs",AllowMultiple=true,FileTypeFilter=[new("Java archive"){Patterns=["*.jar"]}]});
            if(files.Count>0)await model.ImportModsAsync(files.Select(x=>x.TryGetLocalPath()).OfType<string>());
        }
        catch(Exception e){model.Status=e.Message;}
    }
    async void EnableMod(object? sender,RoutedEventArgs args)=>await model.SetModEnabledAsync(true);
    async void DisableMod(object? sender,RoutedEventArgs args)=>await model.SetModEnabledAsync(false);
    async void RestoreBackup(object? sender,RoutedEventArgs args)
    {
        if(model.SelectedBackup is null){model.Status="Select a backup to restore.";return;}
        if(await Confirm("Restore this backup?","This replaces the selected stopped server’s content. A recovery backup is created first. Its identity stays unchanged.","Restore backup"))await model.RestoreBackupAsync();
    }
    async Task<bool> Confirm(string title,string message,string action)
    {
        var dialog=new Window {Title=title,Width=470,SizeToContent=SizeToContent.Height,CanResize=false,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var yes=new Button {Content=action};yes.Classes.Add("primary");var no=new Button {Content="Cancel"};
        yes.Click+=(_,_)=>dialog.Close(true);no.Click+=(_,_)=>dialog.Close(false);
        dialog.Content=new StackPanel {Margin=new(24),Spacing=20,Children={new TextBlock {Text=title,FontSize=21,FontWeight=Avalonia.Media.FontWeight.SemiBold},new TextBlock {Text=message,TextWrapping=Avalonia.Media.TextWrapping.Wrap},new StackPanel {Orientation=Avalonia.Layout.Orientation.Horizontal,Spacing=12,Children={no,yes}}}};
        return await dialog.ShowDialog<bool>(this);
    }
    async void OnClosing(object? sender,WindowClosingEventArgs e)
    {
        if(closeAllowed)return;
        if(model.Busy)
        {
            e.Cancel=true;
            if(await Confirm("An operation is still in progress","Cancel the current operation? Harbor will remain open until it has finished cleanup. Close the window again after the operation completes.","Cancel operation"))model.CancelCommand.Execute(null);
            return;
        }
        var running=model.Servers.Where(x=>model.Lifecycle.GetStatus(x.Id).State is ServerState.Starting or ServerState.Running or ServerState.Stopping).ToArray();
        if(running.Length==0)return;
        e.Cancel=true;
        if(!await Confirm("Stop servers and quit?","Harbor will wait for graceful shutdown. Closing the lid or suspending your laptop interrupts hosting.","Stop all and quit"))return;
        try
        {
            model.Status="Stopping all servers before quitting…";
            using var shutdown=new CancellationTokenSource(TimeSpan.FromSeconds(60));
            foreach(var s in running)await model.Lifecycle.StopAsync(s.Id,shutdown.Token);
            closeAllowed=true;await model.Lifecycle.DisposeAsync();Close();
        }
        catch(Exception error){model.Status="Shutdown incomplete: "+error.Message+". The app remains open to protect your worlds.";}
    }
}
