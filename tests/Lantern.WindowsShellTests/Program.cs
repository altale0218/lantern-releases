using Lantern.AppLogic;
using Lantern.Windows;

internal static class Program
{
    [STAThread] static void Main(string[] args)
    {
        void Assert(bool value,string text){if(!value)throw new Exception(text);}
        var root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("LANTERN_DATA_DIR",root);
        // Keep a detached copy; do not print or persist the user's clipboard contents.
        var saved=new DataObject();var original=Clipboard.GetDataObject();
        if(original is not null)foreach(string format in original.GetFormats(false))try{var data=original.GetData(format,false);if(data is not null)saved.SetData(format,false,data);}catch{}
        try
        {
            using var window=new Form{Text="Lantern Windows Shell test",Width=360,Height=180,ShowInTaskbar=true};window.Show();Application.DoEvents();
            var host=new WindowsHost(window);
            string file=Path.Combine(root,"copy-test.txt");File.WriteAllText(file,"File clipboard fixture");
            host.CopyLocalFileAsync(file,false).GetAwaiter().GetResult();
            Assert(Clipboard.ContainsFileDropList()&&Clipboard.GetFileDropList().Cast<string>().SequenceEqual([file]),"Native file clipboard");
            var effect=Clipboard.GetData("Preferred DropEffect") as MemoryStream;
            Assert(effect is not null&&BitConverter.ToInt32(effect.ToArray())==1,"Copy effect, never move");
            string destination=Path.Combine(root,"paste");Directory.CreateDirectory(destination);
            foreach(string source in Clipboard.GetFileDropList().Cast<string>())File.Copy(source,Path.Combine(destination,Path.GetFileName(source)),true);
            Assert(File.Exists(file)&&File.ReadAllText(Path.Combine(destination,"copy-test.txt"))=="File clipboard fixture","Paste copies bytes and preserves source");
            bool rejected=false;try{host.CopyLocalFileAsync(Path.Combine(root,"missing.txt"),false).GetAwaiter().GetResult();}catch(IOException){rejected=true;}Assert(rejected,"Missing file rejected");
            rejected=false;try{host.CopyLocalFileAsync(file,true).GetAwaiter().GetResult();}catch(IOException){rejected=true;}Assert(rejected,"Unregistered received path rejected");
            using var taskbar=new TaskbarProgress();taskbar.ButtonCreated();
            var task=new TransferNotificationTask("test","transferring","send",100,50,1);
            TransferNotificationSnapshot Snapshot(string state)=>new(false,false,false,[task with{State=state}]);
            taskbar.Update(window.Handle,Snapshot("transferring"),true);Assert(taskbar.AppliedFrame?.State==TaskbarState.Normal,"Explorer native COM progress applied");
            window.WindowState=FormWindowState.Minimized;Application.DoEvents();
            taskbar.Update(window.Handle,Snapshot("paused"),true);Assert(taskbar.AppliedFrame?.State==TaskbarState.Paused,"Minimized progress paused");
            taskbar.ButtonCreated();taskbar.Update(window.Handle,Snapshot("transferring"),true);Assert(taskbar.AppliedFrame?.State==TaskbarState.Normal,"Taskbar recreation reapplies progress");
            taskbar.Update(window.Handle,Snapshot("completed"),true);Assert(taskbar.AppliedFrame?.State==TaskbarState.None,"Completion clears real taskbar");
            window.Close();Console.WriteLine("PASS native Windows Shell: FileDrop clipboard, COPY effect, paste bytes/source retained, missing/unowned paths rejected; Explorer COM progress, minimized pause, taskbar recreation and completion clearing.");
        }
        finally{if(saved.GetFormats().Length>0)Clipboard.SetDataObject(saved,true);else Clipboard.Clear();}
    }
}
