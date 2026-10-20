using System;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Windows.Forms;

namespace Rbx2Source
{
    static class Program
    {
        private static bool validateCert(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errors)
        {
            return (errors == SslPolicyErrors.None);
        }

        [STAThread]
        static int Main(string[] args)
        {
            // Run by RoPorter itself to set a Workshop item's description and page image (see Export.SteamPreview).
            if (args.Length > 0 && args[0] == Export.Workshop.DetailsArgument)
                return Export.SteamPreview.Run(args);
            // Run by RoPorter itself to publish a Source Filmmaker export to SFM's Workshop (see Export.SfmWorkshop).
            if (args.Length > 0 && args[0] == Export.SfmWorkshop.PublishArgument)
                return Export.SfmWorkshop.Run(args);

            var httpsValidator = new RemoteCertificateValidationCallback(validateCert);
            ServicePointManager.ServerCertificateValidationCallback += httpsValidator;
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Rbx2Source());
            return 0;
        }
    }
}
