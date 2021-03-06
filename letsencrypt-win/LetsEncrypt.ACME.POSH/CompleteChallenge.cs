﻿using LetsEncrypt.ACME.DNS;
using LetsEncrypt.ACME.POSH.Util;
using LetsEncrypt.ACME.WebServer;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LetsEncrypt.ACME.POSH
{
    [Cmdlet(VerbsLifecycle.Complete, "Challenge")]
    public class CompleteChallenge : Cmdlet
    {
        [Parameter(Mandatory = true)]
        public string Ref
        { get; set; }

        [Parameter(Mandatory = true)]
        [ValidateSet("dns", "simpleHttp")]
        public string Challenge
        { get; set; }

        [Parameter(Mandatory = true)]
        public string ProviderConfig
        { get; set; }

        [Parameter]
        public SwitchParameter Regenerate
        { get; set; }

        [Parameter]
        public SwitchParameter Repeat
        { get; set; }

        [Parameter]
        public SwitchParameter UseBaseURI
        { get; set; }

        protected override void ProcessRecord()
        {
            using (var vp = InitializeVault.GetVaultProvider())
            {
                vp.OpenStorage();
                var v = vp.LoadVault();

                if (v.Registrations == null || v.Registrations.Count < 1)
                    throw new InvalidOperationException("No registrations found");

                var ri = v.Registrations[0];
                var r = ri.Registration;

                if (v.Identifiers == null || v.Identifiers.Count < 1)
                    throw new InvalidOperationException("No identifiers found");

                var ii = v.Identifiers.GetByRef(Ref);
                if (ii == null)
                    throw new Exception("Unable to find an Identifier for the given reference");

                var authzState = ii.Authorization;

                if (ii.Challenges == null)
                    ii.Challenges = new Dictionary<string, AuthorizeChallenge>();

                if (ii.ChallengeCompleted == null)
                    ii.ChallengeCompleted = new Dictionary<string, DateTime?>();

                if (v.ProviderConfigs == null || v.ProviderConfigs.Count < 1)
                    throw new InvalidOperationException("No provider configs found");

                var pc = v.ProviderConfigs.GetByRef(ProviderConfig);
                if (pc == null)
                    throw new InvalidOperationException("Unable to find a Provider Config for the given reference");
                var pcFilePath = Path.GetFullPath($"{pc.Id}.json");

                AuthorizeChallenge challenge = null;
                DateTime? challengCompleted = null;
                ii.Challenges.TryGetValue(Challenge, out challenge);
                ii.ChallengeCompleted.TryGetValue(Challenge, out challengCompleted);

                if (challenge == null || Regenerate)
                {
                    using (var c = ClientHelper.GetClient(v, ri))
                    {
                        c.Init();
                        c.GetDirectory(true);

                        challenge = c.GenerateAuthorizeChallengeAnswer(authzState, Challenge);
                        ii.Challenges[Challenge] = challenge;
                    }
                }

                if (Repeat || challengCompleted == null)
                {
                    if (Challenge == "dns")
                    {
                        if (string.IsNullOrEmpty(pc.DnsProvider))
                            throw new InvalidOperationException("Referenced Provider Configuration does not support the selected Challenge");

                        var dnsName = challenge.ChallengeAnswer.Key;
                        var dnsValue = Regex.Replace(challenge.ChallengeAnswer.Value, "\\s", "");
                        var dnsValues = Regex.Replace(dnsValue, "(.{100,100})", "$1\n").Split('\n');

                        using (var fs = new FileStream(pcFilePath, FileMode.Open))
                        {
                            var dnsInfo = DnsInfo.Load(fs);
                            dnsInfo.Provider.EditTxtRecord(dnsName, dnsValues);
                            ii.ChallengeCompleted[Challenge] = DateTime.Now;
                        }
                    }
                    else if (Challenge == "simpleHttp")
                    {
                        if (string.IsNullOrEmpty(pc.WebServerProvider))
                            throw new InvalidOperationException("Referenced Provider Configuration does not support the selected Challenge");

                        var wsFilePath = challenge.ChallengeAnswer.Key;
                        var wsFileBody = challenge.ChallengeAnswer.Value;
                        var wsFileUrl = new Uri($"http://{authzState.Identifier}/{wsFilePath}");

                        using (var fs = new FileStream(pcFilePath, FileMode.Open))
                        {
                            var webServerInfo = WebServerInfo.Load(fs);
                            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(wsFileBody)))
                            {
                                webServerInfo.Provider.UploadFile(wsFileUrl, ms);
                                ii.ChallengeCompleted[Challenge] = DateTime.Now;
                            }
                        }
                    }
                }

                vp.SaveVault(v);

                WriteObject(authzState);
            }
        }
    }
}
