using System.IO;
using System.Text.Json;

/// <summary>Atomic per-run fixture messages. Readers allow replacement while their
/// old file handle remains open; bounded retries cover antivirus/indexer transients.</summary>
internal static class CaptureFixtureIpc
{
    internal static string ReadAllText(string path)
    {
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        using var reader=new StreamReader(stream);return reader.ReadToEnd();
    }
    internal static void WriteJson(string path,object value)
    {
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".pending";
        File.WriteAllText(temporary,JsonSerializer.Serialize(value));
        try
        {
            for(int attempt=0;;attempt++)
            {
                try{File.Move(temporary,path,true);return;}
                catch(IOException)when(attempt<8){Thread.Sleep(2);}
                catch(UnauthorizedAccessException)when(attempt<8){Thread.Sleep(2);}
            }
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}
