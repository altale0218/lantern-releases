using Lantern.Windows;
void Assert(bool value,string text){if(!value)throw new Exception(text);}
void Reject(Action action){try{action();}catch(IOException){return;}throw new Exception("Expected path rejection");}
string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
string data=Path.Combine(root,"app"),downloads=Path.Combine(root,"Downloads"),custom=Path.Combine(root,"custom");
Directory.CreateDirectory(downloads);
var storage=new WindowsReceiveStorage(data,downloads);
Assert(storage.Location==downloads,"Default follows supplied known Downloads folder");
string task=Path.Combine(downloads,"Transfer-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(task);
string file=Path.Combine(task,"abcdef01-demo.txt");File.WriteAllText(file,"verified data");storage.Register(task);
storage.SetLocation(custom);
Assert(File.ReadAllText(storage.ValidateFile(file))=="verified data","Changing destination preserves old task access");
storage=new WindowsReceiveStorage(data,downloads);
Assert(storage.Location==custom&&storage.ValidateFolder(task)==task,"Destination and ownership survive restart");
storage.SetLocation(null);Assert(storage.Location==downloads,"Reset resolves Downloads, leaves existing files in place");
string unrelated=Path.Combine(downloads,"Transfer-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(unrelated);File.WriteAllText(Path.Combine(unrelated,"keep.txt"),"keep");
Reject(()=>storage.DeleteFolder(unrelated));Reject(()=>storage.ValidateFile(Path.Combine(custom,"outside.txt")));Reject(()=>storage.DeleteFolder(downloads));
Assert(File.Exists(Path.Combine(unrelated,"keep.txt")),"Unregistered and root directories cannot be deleted");
string legacy=Path.Combine(data,"Inbox","Transfer-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(legacy);File.WriteAllText(Path.Combine(legacy,"legacy.txt"),"old");
Assert(storage.ValidateFolder(legacy)==legacy,"Old private Inbox records remain accessible");
storage.DeleteFolder(task);Assert(!Directory.Exists(task)&&Directory.Exists(downloads)&&Directory.Exists(unrelated),"Delete only owned task, preserve destination and unrelated data");
string linked=Path.Combine(custom,"Transfer-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(linked);Directory.CreateDirectory(Path.Combine(linked,"added"));
storage.Register(legacy);Reject(()=>storage.Register(linked));
Console.WriteLine("PASS: Downloads default, custom/reset persistence, old-location access, legacy Inbox, owned-task deletion and unrelated/root/subdirectory protection");
