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
        var running=model.Servers.Where(x=>model.Lifecycle.GetStatus(x.Id).State is ServerState.Starting or ServerState.Running or ServerState.Stopping).ToArray();
        if(running.Length==0)return;
        e.Cancel=true;
        if(!await Confirm("Stop servers and quit?","Harbor will wait for graceful shutdown. Closing the lid or suspending your laptop interrupts hosting.","Stop all and quit"))return;
        try
        {
            model.Status="Stopping all servers before quitting…";
            foreach(var s in running)await model.Lifecycle.StopAsync(s.Id);
            closeAllowed=true;await model.Lifecycle.DisposeAsync();Close();
        }
        catch(Exception error){model.Status="Shutdown incomplete: "+error.Message+". The app remains open to protect your worlds.";}
    }
}
