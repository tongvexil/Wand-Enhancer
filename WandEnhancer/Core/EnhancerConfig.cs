using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using WandEnhancer.Models;

namespace WandEnhancer.Core
{
    public static class EnhancerConfig
    {
        public class ResolveContext
        {
            public string Placeholder { get; set; }
            public Func<string, string> Handler { get; set; }
        }

        public class PatchEntry
        {
            public Regex Target { get; set; }
            public string Patch { get; set; }
            public Func<Match, string> PatchFactory { get; set; }
            public string Name { get; set; }
            public bool Applied { get; set; }
            public bool SingleMatch { get; set; } = true;
            public string[] CandidateFileNames { get; set; }
            public string[] SearchHints { get; set; }
            public ResolveContext Resolver { get; set; }
        }

        private static string RequireGroup(Match match, string groupName, string patchName)
        {
            var group = match.Groups[groupName];
            if (!group.Success || string.IsNullOrEmpty(group.Value))
            {
                throw new Exception($"{patchName} failed to resolve {groupName}");
            }

            return group.Value;
        }

        private static string BuildSetAccountLanguagePatch(Match match)
        {
            var parameters = RequireGroup(match, "params", "setAccountLanguage");
            var expr = RequireGroup(match, "expr", "setAccountLanguage");
            return $"setAccountLanguage({parameters}){{return ({expr}).then(response=>{{response&&\"object\"==typeof response&&(response.subscription={{period:\"yearly\",state:\"active\"}});return response;}})}}";
        }

        private static string BuildSetAccountReducerPatch(Match match)
        {
            var decl = RequireGroup(match, "decl", "setAccountReducer");
            var fn = RequireGroup(match, "fn", "setAccountReducer");
            var parameters = RequireGroup(match, "params", "setAccountReducer");
            var state = RequireGroup(match, "state", "setAccountReducer");
            var account = RequireGroup(match, "account", "setAccountReducer");
            return
                $"const {decl}=\"ACTION_SET_ACCOUNT\";function {fn}({parameters}){{const a={account}&&\"object\"==typeof {account}?{{...{account},subscription:{{period:\"yearly\",state:\"active\"}}}}:{account};return{{...{state},account:a}}}}";
        }

        public static Dictionary<EPatchType, PatchEntry[]> GetInstance()
        {
            return new Dictionary<EPatchType, PatchEntry[]>()
            {
                {
                    EPatchType.ActivatePro,
                    new[]
                    {
                        new PatchEntry
                        {
                            SearchHints = new[] { "getUserAccount()", "/v3/account" },
                            Resolver = new ResolveContext
                            {
                                Handler = (targetFunction) =>
                                {
                                    var fetchMatch = Regex.Match(targetFunction, @"return\s+this\.#(\w+)\.fetch");
                                    return fetchMatch.Success ? fetchMatch.Groups[1].Value : null;
                                },
                                Placeholder = "<service_name>"
                            },
                            Name = "getUserAccount",
                            Target = new Regex(@"getUserAccount\(\)\{.*?return\s+this\.#\w+\.fetch\(\{.*?\}\)\}",
                                RegexOptions.Singleline),
                            Patch =
                                "getUserAccount(){return this.#<service_name>.fetch({endpoint:\"/v3/account\",method:\"GET\",name:\"/v3/account\",collectMetrics:0}).then(response=>{response.subscription={period:\"yearly\",state:\"active\"};return response;})}"
                        },
                        new PatchEntry
                        {
                            SearchHints = new[] { "setAccountWandBrandExperience()", "/v3/account/brand_experience_wand" },
                            Resolver = new ResolveContext
                            {
                                Handler = (targetFunction) =>
                                {
                                    var match = Regex.Match(targetFunction, @"return\s+this\.#(\w+)\.post");
                                    return match.Success ? match.Groups[1].Value : null;
                                },
                                Placeholder = "<service_name>"
                            },
                            Name = "setAccountWandBrandExperience",
                            Target = new Regex(
                                @"setAccountWandBrandExperience\(\)\{.*?return\s+this\.#\w+\.post\(""/v3/account/brand_experience_wand""\)\}",
                                RegexOptions.Singleline),
                            Patch =
                                "setAccountWandBrandExperience(){return this.#<service_name>.post(\"/v3/account/brand_experience_wand\").then(response=>{response.subscription={period:\"yearly\",state:\"active\"};return response;})}"
                        },
                        new PatchEntry
                        {
                            // Account-returning endpoint the original patches missed: changing
                            // language dispatches its (non-Pro) response into the store and
                            // wiped Pro. Wrap the result the same way. Param names are captured
                            // so the rewritten body keeps the real argument identifiers.
                            Name = "setAccountLanguage",
                            SearchHints = new[] { "setAccountLanguage(", "/v3/account/language" },
                            Target = new Regex(
                                @"setAccountLanguage\((?<params>[^)]*)\)\{\s*return\s+(?<expr>this\.#\w+\.post\(""/v3/account/language"",\{[^}]*\}\))\s*;?\s*\}",
                                RegexOptions.Singleline),
                            PatchFactory = BuildSetAccountLanguagePatch
                        },
                        new PatchEntry
                        {
                            // Last-resort guard: any code path that dispatches ACTION_SET_ACCOUNT
                            // (periodic refreshAccount, push updates, profile edits, etc.) must keep
                            // subscription on the store object even when it bypasses the account API
                            // service methods patched above.
                            Name = "setAccountReducer",
                            SearchHints = new[] { "ACTION_SET_ACCOUNT" },
                            Target = new Regex(
                                @"const (?<decl>\w+)=""ACTION_SET_ACCOUNT"";function (?<fn>\w+)\((?<params>[^)]*)\)\{return\{\.\.\.(?<state>\w+),account:(?<account>\w+)\}\}",
                                RegexOptions.Singleline),
                            PatchFactory = BuildSetAccountReducerPatch
                        },
                        new PatchEntry
                        {
                            // Wand's native "connect phone" pairing (POST /v3/auth/remote_code)
                            // triggers a server-side device handoff that deauthorizes this desktop
                            // session - the reported "entered the mobile activation key and got
                            // signed out" bug. Neutralize the code issuer so native pairing can
                            // never start. The rejection is swallowed by the caller's try/catch
                            // and renders no code.
                            Name = "disableNativeRemotePairing",
                            SearchHints = new[] { "requestRemoteAuthCode", "/v3/auth/remote_code" },
                            Target = new Regex(@"requestRemoteAuthCode\(\)\{return this\.#[\w$]+\.post\(""/v3/auth/remote_code""\)\}"),
                            Patch = "requestRemoteAuthCode(){return Promise.reject(new Error(\"wand-enhancer: native mobile pairing disabled\"))}"
                        }
                    }
                },
                {
                    EPatchType.DisableUpdates,
                    new[]
                    {
                        // Regex consumes 4 closing parens (`)))) `); the 5th (registerHandler's own close)
                        // remains in the original file after replacement. Patch must end with 3 parens — NOT 4.
                        new PatchEntry
                        {
                            CandidateFileNames = new[] { "index.js" },
                            SearchHints = new[] { "ACTION_CHECK_FOR_UPDATE" },
                            Target = new Regex(@"registerHandler\(""ACTION_CHECK_FOR_UPDATE"".*?\)\)\)\)",
                                RegexOptions.Singleline),
                            Patch = "registerHandler(\"ACTION_CHECK_FOR_UPDATE\",(e=>expectUpdateFeedUrl(e,(e=>null)))"
                        }
                    }
                },
                {
                    EPatchType.DevToolsOnF12,
                    new[]
                    {
                        new PatchEntry
                        {
                            Name = "devToolsBeforeInputEvent",
                            CandidateFileNames = new[] { "index.js" },
                            SearchHints = new[] { "whenReady().then(" },
                            // Anchor on the Electron main-process `<app>.whenReady().then(`
                            // call. This site is far more stable than the minified renderer
                            // keydown listener that previously held the F12 -> ACTION_OPEN_DEV_TOOLS
                            // dispatch (its identifiers and shape change on every Wand release).
                            // We attach a `before-input-event` hook to every BrowserWindow's
                            // webContents which toggles DevTools on F12 directly from the main
                            // process, bypassing the renderer dispatcher entirely.
                            Target = new Regex(@"(?<app>\w+)\.whenReady\(\)\.then\("),
                            Patch = "${app}.on(\"browser-window-created\",((_,w)=>{try{w.webContents.on(\"before-input-event\",((_,i)=>{if(\"F12\"===i.key&&\"keyDown\"===i.type){w.webContents.isDevToolsOpened()?w.webContents.closeDevTools():w.webContents.openDevTools({mode:\"detach\"})}}))}catch(e){}})),${app}.whenReady().then("
                        }
                    }
                }
            };
        }
    }
}
