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
   var read=type.GetMethod("ReadItem",BindingFlags.NonPublic|BindingFlags.Static);var add=type.GetMethod("AddRecycledFiles",BindingFlags.NonPublic|BindingFlags.Instance);
   add.Invoke(form,new[]{read.Invoke(null,new object[]{dirPayload})});add.Invoke(form,new[]{read.Invoke(null,new object[]{filePayload})});add.Invoke(form,new[]{read.Invoke(null,new object[]{imagePayload})});
   var items=(IList)type.GetField("_items",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(form);Assert(items.Count==9,"expected scripts and non-script files, including unknown types");
   var itemType=items[0].GetType(); Func<object,string,string> field=(x,n)=>(string)itemType.GetField(n).GetValue(x);
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
   ((Form)form).Dispose();Console.WriteLine("fixture scan tests passed");
  }catch(Exception ex){Console.Error.WriteLine(ex.Message);Environment.ExitCode=1;}
  finally{Directory.Delete(dir,true);}
 }
}
