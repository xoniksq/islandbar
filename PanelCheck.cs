using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace IslandBar;

sealed partial class IslandWindow
{
    internal async Task CheckPanelsAsync()
    {
        var results = new Dictionary<string, bool>();
        try {
            Expand();
            await Task.Delay(350);
            foreach (var (name, open) in new (string, Action)[] {
                ("calendar", OpenCalendar), ("folders", OpenFolders), ("downloads", OpenDownloads),
                ("pc", OpenPc), ("network", OpenNetwork), ("sets", OpenSets), ("music", OpenMusic),
                ("sound", OpenSound), ("mixer", OpenMixer), ("bluetooth", OpenBluetooth),
                ("layouts", OpenLayouts), ("settings", OpenSettings)
            }) {
                open();
                await Task.Delay(100);
                results[name] = _featureKind == name && _featurePopup.Child is FrameworkElement { ActualHeight: > 0 };
                CloseFeature(immediate: true);
            }
            foreach (var device in await BluetoothService.GetPairedAsync(CancellationToken.None)) {
                Program.Log($"Bluetooth audio driver {device.Name}: connect={BluetoothAudio.Request(device.Key, true, true)} disconnect={BluetoothAudio.Request(device.Key, false, true)}");
            }
            await OpenTray();
            results["tray"] = _featureKind == "tray" && _featurePopup.IsOpen;
            CloseFeature(immediate: true);
            _overflowButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            results["overflow"] = _featureKind == "overflow";
            CloseFeature(immediate: true);
            OpenStorage();
            if (_storageWindow is not {} storage) throw new InvalidOperationException("Storage window unavailable");
            await storage.CheckSectionsAsync(results);
            storage.Close();
            CheckTransfers(results);
        }
        catch (Exception ex) { results["Completed"] = false; Program.Log("panel check: "+ex); }
        File.WriteAllText(Path.Combine(Program.Folder,"panel-check.json"),JsonSerializer.Serialize(results));
        Application.Current.Shutdown(results.Values.All(ok => ok) ? 0 : 1);
    }

    static void CheckTransfers(Dictionary<string,bool> results)
    {
        var root = Directory.CreateTempSubdirectory("IslandBar-");
        try {
            var destination = root.CreateSubdirectory("destination");
            string source = Path.Combine(root.FullName,"sample.txt");
            File.WriteAllText(source,"IslandBar test");
            var transfers = new StorageTransfers(Path.Combine(root.FullName,"journal.json"));
            var entry = StorageCatalog.Entry(new FileInfo(source));
            var copy = transfers.Execute(new(entry,destination.FullName,false));
            results["copy"] = copy.Completed && File.ReadAllText(copy.Destination)==File.ReadAllText(source);
            File.Delete(copy.Destination);
            var move = transfers.Execute(new(entry,destination.FullName,true));
            results["move"] = move.Completed && !File.Exists(source);
            var undo = transfers.Undo(move.Id);
            results["undo"] = undo.Completed && File.ReadAllText(source)=="IslandBar test";
            string target = StorageBatchRename.Targets([entry],"item").Single();
            StorageBatchRename.Run([new(source,target,StorageTransfers.GetItemIdentity(source)!)]);
            results["rename"] = File.Exists(target) && !File.Exists(source);
        }
        finally { root.Delete(true); }
    }
}

sealed partial class StorageWindow
{
    internal async Task CheckSectionsAsync(Dictionary<string,bool> results)
    {
        foreach (string name in SectionNames) {
            _sections[name].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(150);
            results[name] = _view==name && _page.Content is FrameworkElement { IsLoaded:true };
        }
    }
}
