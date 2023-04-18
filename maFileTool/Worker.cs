using System;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using MailKit;
using MailKit.Net.Imap;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SteamAuth;

namespace maFileTool
{
    public class Worker
    {
        private readonly string _onlineSimApiKey = File.ReadAllText("onlinesimkey.txt");
        private readonly string _mailImap = File.Exists("imap.txt") ? File.ReadAllText("imap.txt") : "imap.rambler.ru";

        private readonly string _login;
        private readonly string _password;
        private readonly string _emailLogin;
        private readonly string _emailPassword;
        private string _phoneNumber;
        private string _tzId;

        public Worker(string login, string password, string emailLogin, string emailPassword)
        {
            _login = login;
            _password = password;
            _emailLogin = emailLogin;
            _emailPassword = emailPassword;
        }
        
        public void DoWork()
        {
            try
            {
                var userLogin = new UserLogin(_login, _password);
                var response = LoginResult.BadCredentials;

                while ((response = userLogin.DoLogin()) != LoginResult.LoginOkay)
                {
                    switch (response)
                    {
                        case LoginResult.NeedEmail:
                            Log("Waiting login code");
                            var emailCode = GetLoginCodeRambler();
                            userLogin.EmailCode = emailCode;
                            break;

                        case LoginResult.NeedCaptcha:
                            Log("Steam captcha");
                            return;

                        case LoginResult.Need2FA:
                            Log("Already 2FA protected");
                            return;

                        case LoginResult.BadRSA:
                            Log("Error logging in: Steam returned \"BadRSA\"");
                            return;

                        case LoginResult.BadCredentials:
                            Log("Wrong username or password");
                            return;

                        case LoginResult.TooManyFailedLogins:
                            Log("IP banned");
                            return;

                        case LoginResult.GeneralFailure:
                            Log("Steam GeneralFailture :(");
                            return;
                    }
                }

                var session = userLogin.Session;
                 var linker = new AuthenticatorLinker(session);
                 var linkResponse = AuthenticatorLinker.LinkResult.GeneralFailure;

                 while ((linkResponse = linker.AddAuthenticator()) != AuthenticatorLinker.LinkResult.AwaitingFinalization)
                 {
                     switch (linkResponse)
                     {
                         case AuthenticatorLinker.LinkResult.MustProvidePhoneNumber:
                             var phoneNumber = GetNewPhoneNumber();
                             phoneNumber = FilterPhoneNumber(phoneNumber);
                             linker.PhoneNumber = phoneNumber;
                             break;

                         case AuthenticatorLinker.LinkResult.MustRemovePhoneNumber:
                             linker.PhoneNumber = null;
                             break;

                         case AuthenticatorLinker.LinkResult.MustConfirmEmail:
                             ConfirmPhoneRambler();
                             break;

                         case AuthenticatorLinker.LinkResult.GeneralFailure:
                             Log("Steam GeneralFailture :(");
                             return;
                     }
                 }

                 var finalizeResponse = AuthenticatorLinker.FinalizeResult.GeneralFailure;
                 while (finalizeResponse != AuthenticatorLinker.FinalizeResult.Success)
                 {
                     var smsCode = WaitSmsCode();

                     if (string.IsNullOrEmpty(smsCode))
                     {
                         Log("Bad SMS code");
                         return;
                     }

                     finalizeResponse = linker.FinalizeAddAuthenticator(smsCode);

                     switch (finalizeResponse)
                     {
                         case AuthenticatorLinker.FinalizeResult.BadSMSCode:
                             Log("SMS code incorrect");
                             return;

                         case AuthenticatorLinker.FinalizeResult.UnableToGenerateCorrectCodes:
                             Log("Unable to generate the proper codes to finalize this authenticator. The authenticator should not have been linked. In the off-chance it was, please write down your revocation code, as this is the last chance to see it: " + linker.LinkedAccount.RevocationCode);
                             return;

                         case AuthenticatorLinker.FinalizeResult.GeneralFailure:
                             Log("Steam GeneralFailture :(");
                             return;
                     }
                 }

                 SaveAccount(linker.LinkedAccount);
                 Log($"{_login}:{_password}:{_emailLogin}:{_emailPassword}:{_phoneNumber}:{linker.LinkedAccount.RevocationCode}");
                 LogToFile($"{_login}:{_password}:{_emailLogin}:{_emailPassword}:{_phoneNumber}:{linker.LinkedAccount.RevocationCode}");
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }

        private void ConfirmPhoneRambler()
        {
            Thread.Sleep(10000);
            
            using (var client = new ImapClient())
            {
                client.Connect(_mailImap, 993, true);
                client.Authenticate(_emailLogin, _emailPassword);

                var inbox = client.Inbox;
                inbox.Open(FolderAccess.ReadWrite);

                for (var i = inbox.Count - 1; i >= 0; i--)
                {
                    var message = inbox.GetMessage(i);
                    var link = Regex.Match(message.HtmlBody, "store([.])steampowered([.])com([\\/])phone([\\/])ConfirmEmailForAdd([?])stoken=([^\"]+)").Groups[0].Value;
                    if (string.IsNullOrEmpty(link)) continue;
                    
                    new WebClient().DownloadString("https://" + link);
                    Log("Mail confirmed");
                    break;
                }

                client.Disconnect(true);
            }
            
            Thread.Sleep(2000);
        }

        private string GetLoginCodeRambler()
        {
            Thread.Sleep(10000);
            
            var loginCode = string.Empty;

            using (var client = new ImapClient())
            {
                client.Connect(_mailImap, 993, true);
                client.Authenticate(_emailLogin, _emailPassword);
                
                var inbox = client.Inbox;
                inbox.Open(FolderAccess.ReadWrite);

                for (var i = inbox.Count - 1; i >= 0; i--)
                {
                    var message = inbox.GetMessage(i);

                    var code = Regex.Match(message.HtmlBody, "class=([\"])title-48 c-blue1 fw-b a-center([^>]+)([>])([^<]+)").Groups[4].Value;
                    if (string.IsNullOrEmpty(code)) continue;

                    loginCode = code.Trim();
                    Log($"Login code: {loginCode}");
                    break;
                }
            }

            return loginCode;
        }

        private string GetNewPhoneNumber()
        {
            using (var wc = new WebClient())
            {
                var response = wc.DownloadString($"https://onlinesim.io/api/getNum.php?apikey={_onlineSimApiKey}&service=Steam&country=372");
                var json = JObject.Parse(response);
                
                if (json["response"]?.ToString() == "1")
                    _tzId = json["tzid"]?.ToString();
                else return string.Empty;
            }

            Thread.Sleep(5000);

            using (var wc = new WebClient())
            {
                var response = wc.DownloadString($"https://onlinesim.io/api/getState.php?apikey={_onlineSimApiKey}&tzid={_tzId}").Replace("[", string.Empty).Replace("]", string.Empty);
                var json = JObject.Parse(response);
                
                _phoneNumber = json["number"]?.ToString();
            }
            
            Log($"Got number: {_phoneNumber}");

            return _phoneNumber;
        }
        
        private string WaitSmsCode()
        {
            var waiting = 0;
            while (waiting < 30)
            {
                Thread.Sleep(2000);
                waiting++;
                
                using (var wc = new WebClient())
                {
                    var response = wc.DownloadString($"https://onlinesim.io/api/getState.php?apikey={_onlineSimApiKey}&tzid={_tzId}&msg_list=0&clean=1&message_to_code=1").Replace("[", string.Empty).Replace("]", string.Empty);
                    var json = JObject.Parse(response);

                    try
                    {
                        var msg = json["msg"]?.ToString();
                        
                        if (string.IsNullOrEmpty(msg))
                            continue;
                        
                        Log($"SMS code: {msg}");
                        
                        return msg;
                    }
                    catch {}
                }
            }

            return string.Empty;
        }
        
        
        private static string FilterPhoneNumber(string phoneNumber) => phoneNumber.Replace("-", string.Empty).Replace("(", string.Empty).Replace(")", string.Empty);
        private static void SaveAccount(SteamGuardAccount account)
        {
            var filename = account.Session.SteamID.ToString() + ".maFile";
            var jsonAccount = JsonConvert.SerializeObject(account);

            Directory.CreateDirectory("maFiles");
            File.WriteAllText("maFiles/" + filename, jsonAccount);
        }

        private void Log(string message) => Console.WriteLine($"[{_login}] {message}");
        private static void LogToFile(string message) => File.AppendAllText("result.txt", message + "\n");
    }
}