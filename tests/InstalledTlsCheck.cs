using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

internal static class InstalledTlsCheck
{
    private static void Main()
    {
        byte[] publicRoot;
        using(var web=new WebClient()){web.Proxy=null;publicRoot=web.DownloadData("http://127.0.0.1:49732/remote-ca.cer");}
        using(var root=new X509Certificate2(publicRoot))
        using(var client=new TcpClient("127.0.0.1",49732))
        using(var tls=new SslStream(client.GetStream(),false,(sender,cert,chain,errors)=> {
            if(cert==null||(errors&SslPolicyErrors.RemoteCertificateNameMismatch)!=0)return false;
            using(var verifier=new X509Chain()) {
                verifier.ChainPolicy.ExtraStore.Add(root);verifier.ChainPolicy.RevocationMode=X509RevocationMode.NoCheck;
                verifier.ChainPolicy.VerificationFlags=X509VerificationFlags.AllowUnknownCertificateAuthority;
                return verifier.Build(new X509Certificate2(cert))&&verifier.ChainElements[verifier.ChainElements.Count-1].Certificate.RawData.SequenceEqual(publicRoot);
            }
        })) {
            tls.ReadTimeout=5000;tls.AuthenticateAsClient("127.0.0.1",null,SslProtocols.Tls12,false);
            byte[] request=Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: 127.0.0.1:49732\r\n\r\n");tls.Write(request,0,request.Length);
            string page=new StreamReader(tls,Encoding.UTF8).ReadToEnd();
            if(!page.StartsWith("HTTP/1.1 200")||!page.Contains("id=\"screenEnabled\"")||!tls.IsEncrypted)throw new Exception("Installed HTTPS interface failed");
            Console.WriteLine("Installed app: verified per-PC certificate chain, TLS 1.2 and encrypted mobile page. No input injected; no trust-store changes.");
        }
        var denied=(HttpWebRequest)WebRequest.Create("http://127.0.0.1:49732/app.js");denied.Proxy=null;denied.Timeout=5000;
        try{using(var response=denied.GetResponse())throw new Exception("Plain controller was served");}
        catch(WebException ex){var response=ex.Response as HttpWebResponse;if(response==null||response.StatusCode!=(HttpStatusCode)426)throw;response.Dispose();}
        Console.WriteLine("Installed app: plaintext controller blocked; public setup remains available.");
    }
}
