using System; using System.IO; using System.Text; using System.Reflection; using System.Collections; using System.Windows.Forms;
class ScanTest {
 static void Assert(bool v,string m){if(!v)throw new Exception(m);}
 static void Meta(string p,string orig){using(var w=new BinaryWriter(File.Create(p))){w.Write((long)2);w.Write((long)1);w.Write(DateTime.UtcNow.ToFileTimeUtc());w.Write(orig.Length+1);w.Write(Encoding.Unicode.GetBytes(orig+"\0"));}}
 [STAThread] static void Main(){
  string dir=Path.Combine(Path.GetTempPath(),"ScriptBinTest-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
  try{
   string dirPayload=Path.Combine(dir,"$Rfolder");Directory.CreateDirectory(dirPayload);Directory.CreateDirectory(Path.Combine(dirPayload,"nested"));
   File.WriteAllText(Path.Combine(dirPayload,"same.py"),"print(1)");File.WriteAllText(Path.Combine(dirPayload,"nested","copy.py"),"print(1)");Meta(Path.Combine(dir,"$Ifolder"),@"C:\project");
   File.WriteAllText(Path.Combine(dirPayload,"run.LOG"),"run output");
   File.WriteAllText(Path.Combine(dirPayload,"output.json"),"{}");
   File.WriteAllText(Path.Combine(dirPayload,"draft.tmp"),"temporary");
   File.WriteAllText(Path.Combine(dirPayload,"README"),"draft notes");
   File.WriteAllText(Path.Combine(dirPayload,"custom.artifact"),"custom data");
   string imagePayload=Path.Combine(dir,"$Rimage.bin");File.WriteAllBytes(imagePayload,new byte[]{1,2,3});Meta(Path.Combine(dir,"$Iimage.bin"),@"D:\other\render.png");
   string filePayload=Path.Combine(dir,"$Rfile.js");File.WriteAllText(filePayload,"print(2)");Meta(Path.Combine(dir,"$Ifile.js"),@"D:\other\same.py");
   var asm=Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ScriptRecycleBin.exe"));var type=asm.GetType("RecycleBinWindow");var form=Activator.CreateInstance(type,BindingFlags.Public|BindingFlags.Instance,null,new object[]{null},null);
   var read=type.GetMethod("ReadItem",BindingFlags.NonPublic|BindingFlags.Static);
   var scanType = type.GetNestedType("ScanSnapshot", BindingFlags.NonPublic);
   var itemArray = Array.CreateInstance(type.GetNestedType("Item", BindingFlags.NonPublic), 0);
   var scan = Activator.CreateInstance(scanType, new object[]{itemArray});
   var add = scanType.GetMethod("AddRecycledFiles", BindingFlags.NonPublic | BindingFlags.Instance);
   var verify = scanType.GetMethod("VerifyNewFiles");
   CheckActionPaths(type);
   add.Invoke(scan,new[]{read.Invoke(null,new object[]{dirPayload})});add.Invoke(scan,new[]{read.Invoke(null,new object[]{filePayload})});add.Invoke(scan,new[]{read.Invoke(null,new object[]{imagePayload})});
   var items=(IList)scanType.GetField("Items").GetValue(scan);Assert(items.Count==9,"expected scripts and non-script files, including unknown types");
   var itemType=items[0].GetType(); Func<object,string,string> field=(x,n)=>(string)itemType.GetField(n).GetValue(x);
   Assert(field(items[0], "Hash") == null, "metadata scan must not hash files before showing the list");
   Assert(verify.Invoke(scan, null) != null, "first scan should verify new files");
   var byPath=new System.Collections.Generic.Dictionary<string,object>();
   foreach(object item in items) byPath.Add(field(item,"Original"),item);
   Assert(field(byPath[@"C:\project\same.py"],"Hash")==field(byPath[@"C:\project\nested\copy.py"],"Hash"),"duplicate hash");
   Assert(field(byPath[@"C:\project\same.py"],"Hash")!=field(byPath[@"D:\other\same.py"],"Hash"),"different content");
   Assert(field(byPath[@"C:\project\run.LOG"],"Category")=="日志","case-insensitive log classification");
   Assert(field(byPath[@"C:\project\output.json"],"Category")=="数据与配置","data classification");
   Assert(field(byPath[@"C:\project\draft.tmp"],"Category")=="缓存与临时文件","temporary classification");
   Assert(field(byPath[@"C:\project\README"],"Category")=="文档","extensionless document classification");
   Assert(field(byPath[@"C:\project\custom.artifact"],"Category")=="其他","unknown classification");
   Assert(field(byPath[@"D:\other\render.png"],"Category")=="图片","top-level classification uses the original filename");
   var refresh = Activator.CreateInstance(scanType, new object[]{items});
   add.Invoke(refresh, new[]{read.Invoke(null, new object[]{dirPayload})});
   Assert(verify.Invoke(refresh, null) == null, "refresh must not hash unchanged files again");
   var changed = Path.Combine(dirPayload, "same.py");
   File.AppendAllText(changed, " # updated");
   var rescan = Activator.CreateInstance(scanType, new object[]{items});
   add.Invoke(rescan, new[]{read.Invoke(null, new object[]{dirPayload})});
   Assert(verify.Invoke(rescan, null) != null, "refresh must hash changed files");
   CheckFileActions(type, read, add, verify, scan, dir);
   ((Form)form).Dispose();Console.WriteLine("fixture scan and action tests passed");
  }catch(Exception ex){Console.Error.WriteLine(ex.Message);Environment.ExitCode=1;}
  finally{Directory.Delete(dir,true);}
 }

 static void CheckActionPaths(Type type)
 {
  var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
  var message = serializer.Deserialize<System.Collections.Generic.Dictionary<string,object>>(
   "{\"cmd\":\"restore\",\"paths\":[\"first\",\"second\",null,42]}");
  var parser = type.GetMethod("ReadActionPaths", BindingFlags.NonPublic | BindingFlags.Static);
  Assert(parser != null, "file actions need a parser for the WebView paths list");
  var paths = (string[])parser.Invoke(null, new object[]{message});
  Assert(paths.Length == 2 && paths[0] == "first" && paths[1] == "second",
   "file actions must receive the paths sent by WebView");
 }

 static void CheckFileActions(Type type, MethodInfo read, MethodInfo add, MethodInfo verify, object scan, string dir)
 {
  string payload = Path.Combine(dir, "$Raction.txt");
  string metadata = Path.Combine(dir, "$Iaction.txt");
  string original = Path.Combine(dir, "restore-target", "action.txt");
  var apply = type.GetMethod("ApplyFileAction", BindingFlags.NonPublic | BindingFlags.Static);
  Assert(apply != null, "file actions must support testing against temporary files");

  File.WriteAllText(payload, "test action");
  Meta(metadata, original);
  object item = read.Invoke(null, new object[]{payload});
  add.Invoke(scan, new[]{item});
  verify.Invoke(scan, null);
  File.SetAttributes(payload, FileAttributes.ReadOnly);
  try
  {
   apply.Invoke(null, new[]{item, "restore", null});
   Assert(File.ReadAllText(original) == "test action", "restore must preserve contents");
   Assert((File.GetAttributes(original) & FileAttributes.ReadOnly) != 0, "restore must preserve read-only attribute");
   Assert(!File.Exists(payload) && !File.Exists(metadata), "restore must remove the recycled item");
  }
  finally
  {
   if (File.Exists(original)) File.SetAttributes(original, FileAttributes.Normal);
   if (File.Exists(payload)) File.SetAttributes(payload, FileAttributes.Normal);
  }

  File.WriteAllText(payload, "test delete");
  Meta(metadata, original);
  item = read.Invoke(null, new object[]{payload});
  add.Invoke(scan, new[]{item});
  verify.Invoke(scan, null);
  File.SetAttributes(payload, FileAttributes.ReadOnly);
  try
  {
   apply.Invoke(null, new[]{item, "delete", null});
   Assert(!File.Exists(payload) && !File.Exists(metadata), "delete must remove a read-only recycled item");
   Assert(File.Exists(original), "delete must leave the original location alone");
  }
  finally
  {
   if (File.Exists(payload)) File.SetAttributes(payload, FileAttributes.Normal);
  }
 }
}
