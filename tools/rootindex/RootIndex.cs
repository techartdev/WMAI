// RootIndex - turns a PEM CA bundle (Mozilla's, from curl.se/ca/cacert.pem)
// into WMAI.roots.idx: one "subjectDN<TAB>base64 DER" line per root. Built with
// the same Bouncy Castle as the phone (vendor/out/ref), so the DN strings match
// what TrustStore computes from a certificate's IssuerDN on the device.
//   RootIndex.exe cacert.pem WMAI.roots.idx
using System;
using System.Collections;
using System.IO;
using System.Text;
using Org.BouncyCastle.X509;

class RootIndex
{
    static int Main(string[] args)
    {
        ICollection certs;
        using (FileStream fs = File.OpenRead(args[0]))
            certs = new X509CertificateParser().ReadCertificates(fs);
        int n = 0, expired = 0;
        using (StreamWriter w = new StreamWriter(args[1], false, new UTF8Encoding(false)))
        {
            w.NewLine = "\n";
            foreach (X509Certificate c in certs)
            {
                if (c.NotAfter < DateTime.UtcNow) { expired++; continue; }
                w.WriteLine(c.SubjectDN.ToString() + "\t" + Convert.ToBase64String(c.GetEncoded()));
                n++;
            }
        }
        Console.WriteLine("{0} roots written ({1} expired skipped) to {2}", n, expired, args[1]);
        return n > 0 ? 0 : 1;
    }
}
