using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Oxide.Core;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("AuthLimit", "emma_smith", "1.2.0")]
    [Description("Limits the maximum number of authorized players on TCs, locks, and turrets to prevent teaming")]
    public class AuthLimit : RustPlugin
    {
        #region Fields

        private PluginConfig _config;
        private const string PermissionBypass = "authlimit.bypass";
        private const string PermissionAdmin = "authlimit.admin";
        private static FieldInfo _codeLockWhitelistField;
        private static FieldInfo _codeLockGuestlistField;
        private readonly Dictionary<string, DateTime> _lastWebhookTime = new Dictionary<string, DateTime>();

        #endregion

        #region Configuration

        private class PluginConfig
        {
            [JsonProperty("Max Authorization Limit")]
            public int MaxAuthLimit { get; set; } = 4;

            [JsonProperty("Enable Debug Logging")]
            public bool DebugLogging { get; set; } = false;

            [JsonProperty("Feature Toggles")]
            public FeatureSettings Features { get; set; } = new FeatureSettings();

            [JsonProperty("Discord Webhook")]
            public DiscordSettings Discord { get; set; } = new DiscordSettings();

            [JsonProperty("Messages")]
            public Dictionary<string, string> Messages { get; set; } = new Dictionary<string, string>();

            public class DiscordSettings
            {
                [JsonProperty("Webhook URL")]
                public string WebhookUrl { get; set; } = "";

                [JsonProperty("Enable Webhook Notifications")]
                public bool EnableWebhook { get; set; } = true;

                [JsonProperty("Webhook Cooldown (Seconds)")]
                public int WebhookCooldown { get; set; } = 60;

                [JsonProperty("Webhook Color (Decimal)")]
                public int WebhookColor { get; set; } = 15158332;
            }

            public class FeatureSettings
            {
                [JsonProperty("Limit Tool Cupboards")]
                public bool LimitTCs { get; set; } = true;

                [JsonProperty("Limit Code Locks")]
                public bool LimitCodeLocks { get; set; } = true;

                [JsonProperty("Limit Auto Turrets")]
                public bool LimitTurrets { get; set; } = true;
            }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null) LoadDefaultConfig();
                SaveConfig();
            }
            catch
            {
                LoadDefaultConfig();
            }
        }

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
            _config.Messages = new Dictionary<string, string>
            {
                ["AuthLimitReached"] = "Authorization limit reached! Max {0} players allowed.",
                ["AuthLimitReachedTC"] = "Tool Cupboard authorization limit reached! Max {0} players allowed.",
                ["AuthLimitReachedCodeLock"] = "Code Lock authorization limit reached! Max {0} players allowed.",
                ["AuthLimitReachedTurret"] = "Auto Turret authorization limit reached! Max {0} players allowed."
            };
        }

        protected override void SaveConfig() => Config.WriteObject(_config);

        #endregion

        #region Discord Webhook

        private void SendDiscordWebhook(string title, string description, List<Dictionary<string, string>> fields, int color)
        {
            if (!_config.Discord.EnableWebhook || string.IsNullOrEmpty(_config.Discord.WebhookUrl)) return;

            var payload = new Dictionary<string, object>
            {
                ["embeds"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["title"] = title,
                        ["description"] = description,
                        ["color"] = color,
                        ["fields"] = fields,
                        ["footer"] = new Dictionary<string, string>
                        {
                            ["text"] = $"AuthLimit v{this.Version} by emma_smith"
                        },
                        ["timestamp"] = DateTime.UtcNow.ToString("o")
                    }
                }
            };

            string json = JsonConvert.SerializeObject(payload);
            webrequest.Enqueue(_config.Discord.WebhookUrl, json, (code, response) =>
            {
                if (code != 200 && code != 204)
                {
                    if (_config.DebugLogging)
                        PrintWarning($"Discord webhook failed: {code} - {response}");
                }
            }, this, Core.Libraries.RequestMethod.POST, new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json"
            });
        }

        private void SendAuthLimitWebhook(BasePlayer player, BaseEntity entity, string entityType, ulong ownerId, int currentAuthCount)
        {
            string cooldownKey = $"{player.userID}:{entityType.Replace(" ", "")}";

            if (_lastWebhookTime.TryGetValue(cooldownKey, out DateTime lastTime))
            {
                var timeSinceLastWebhook = DateTime.UtcNow - lastTime;
                if (timeSinceLastWebhook.TotalSeconds < _config.Discord.WebhookCooldown)
                {
                    if (_config.DebugLogging)
                        Puts($"Webhook cooldown active for {player.displayName} on {entityType} - {_config.Discord.WebhookCooldown - timeSinceLastWebhook.TotalSeconds:F1}s remaining");
                    return;
                }
            }

            _lastWebhookTime[cooldownKey] = DateTime.UtcNow;

            var ownerName = covalence.Players.FindPlayerById(ownerId.ToString())?.Name ?? ownerId.ToString();
            var entityPos = entity.transform.position;
            string teleportCommand = $"teleportpos {entityPos.x:F1} {entityPos.y:F1} {entityPos.z:F1}";

            var fields = new List<Dictionary<string, string>>
            {
                new Dictionary<string, string>
                {
                    ["name"] = "Entity Type",
                    ["value"] = entityType,
                    ["inline"] = "true"
                },
                new Dictionary<string, string>
                {
                    ["name"] = "Current Auth Count",
                    ["value"] = currentAuthCount.ToString(),
                    ["inline"] = "true"
                },
                new Dictionary<string, string>
                {
                    ["name"] = "Max Limit",
                    ["value"] = _config.MaxAuthLimit.ToString(),
                    ["inline"] = "true"
                },
                new Dictionary<string, string>
                {
                    ["name"] = "Entity Owner/Team Leader",
                    ["value"] = $"{ownerName}\n{ownerId}",
                    ["inline"] = "false"
                },
                new Dictionary<string, string>
                {
                    ["name"] = "Player Attempting to Authorize",
                    ["value"] = $"{player.displayName}\n{player.UserIDString}",
                    ["inline"] = "false"
                },
                new Dictionary<string, string>
                {
                    ["name"] = "📍 Teleport to Entity",
                    ["value"] = $"`{teleportCommand}`",
                    ["inline"] = "false"
                }
            };

            SendDiscordWebhook(
                "⚠️ Authorization Limit Exceeded",
                $"**{player.displayName}** attempted to authorize on a **{entityType}** but the limit has been reached.",
                fields,
                _config.Discord.WebhookColor
            );
        }

        #endregion

        #region Oxide Hooks

        private void Init()
        {
            permission.RegisterPermission(PermissionBypass, this);
            permission.RegisterPermission(PermissionAdmin, this);
            lang.RegisterMessages(_config.Messages, this);

            var bindingFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            _codeLockWhitelistField = typeof(CodeLock).GetField("whitelistPlayers", bindingFlags);
            _codeLockGuestlistField = typeof(CodeLock).GetField("guestPlayers", bindingFlags);

            if (_config.DebugLogging)
            {
                Puts($"CodeLock reflection - whitelistPlayers: {(_codeLockWhitelistField != null ? "Found" : "NOT FOUND")}");
                Puts($"CodeLock reflection - guestPlayers: {(_codeLockGuestlistField != null ? "Found" : "NOT FOUND")}");
            }

            timer.Every(300f, () => CleanupWebhookCooldowns());
        }

        private void Unload()
        {
            _lastWebhookTime.Clear();
        }

        private void CleanupWebhookCooldowns()
        {
            var expiredEntries = _lastWebhookTime
                .Where(x => (DateTime.UtcNow - x.Value).TotalSeconds > _config.Discord.WebhookCooldown * 2)
                .Select(x => x.Key)
                .ToList();

            foreach (var playerId in expiredEntries)
            {
                _lastWebhookTime.Remove(playerId);
            }

            if (_config.DebugLogging && expiredEntries.Count > 0)
                Puts($"Cleaned up {expiredEntries.Count} expired webhook cooldown entries");
        }

        private object OnCupboardAuthorize(BuildingPrivlidge privilege, BasePlayer player)
        {
            if (!_config.Features.LimitTCs) return null;
            if (privilege == null || player == null) return null;

            if (permission.UserHasPermission(player.UserIDString, PermissionBypass))
            {
                if (_config.DebugLogging)
                    Puts($"{player.displayName} has bypass permission, allowing TC auth");
                return null;
            }

            if (privilege.IsAuthed(player))
            {
                if (_config.DebugLogging)
                    Puts($"{player.displayName} already authorized on TC");
                return null;
            }

            int currentAuthCount = privilege.authorizedPlayers.Count;

            if (_config.DebugLogging)
                Puts($"TC auth check: Current={currentAuthCount}, Max={_config.MaxAuthLimit}, Player={player.displayName}");

            if (currentAuthCount >= _config.MaxAuthLimit)
            {
                player.ChatMessage(string.Format(lang.GetMessage("AuthLimitReachedTC", this, player.UserIDString), _config.MaxAuthLimit));
                SendAuthLimitWebhook(player, privilege, "Tool Cupboard", privilege.OwnerID, currentAuthCount);

                if (_config.DebugLogging)
                    Puts($"Blocked TC authorization for {player.displayName} - limit reached");

                return false;
            }

            return null;
        }

        private object OnTurretAuthorize(AutoTurret turret, BasePlayer player)
        {
            if (!_config.Features.LimitTurrets) return null;
            if (turret == null || player == null) return null;

            if (permission.UserHasPermission(player.UserIDString, PermissionBypass))
            {
                if (_config.DebugLogging)
                    Puts($"{player.displayName} has bypass permission, allowing turret auth");
                return null;
            }

            if (turret.IsAuthed(player))
            {
                if (_config.DebugLogging)
                    Puts($"{player.displayName} already authorized on turret");
                return null;
            }

            int currentAuthCount = turret.authorizedPlayers.Count;

            if (_config.DebugLogging)
                Puts($"Turret auth check: Current={currentAuthCount}, Max={_config.MaxAuthLimit}, Player={player.displayName}");

            if (currentAuthCount >= _config.MaxAuthLimit)
            {
                player.ChatMessage(string.Format(lang.GetMessage("AuthLimitReachedTurret", this, player.UserIDString), _config.MaxAuthLimit));
                SendAuthLimitWebhook(player, turret, "Auto Turret", turret.OwnerID, currentAuthCount);

                if (_config.DebugLogging)
                    Puts($"Blocked turret authorization for {player.displayName} - limit reached");

                return false;
            }

            return null;
        }

        private object OnCodeEntered(CodeLock codeLock, BasePlayer player, string code, bool isGuestCode)
        {
            if (!_config.Features.LimitCodeLocks) return null;
            if (codeLock == null || player == null) return null;

            if (permission.UserHasPermission(player.UserIDString, PermissionBypass))
            {
                if (_config.DebugLogging)
                    Puts($"{player.displayName} has bypass permission, allowing code lock");
                return null;
            }

            if (!codeLock.code.Equals(code) && !codeLock.guestCode.Equals(code))
            {
                return null;
            }

            if (IsPlayerAuthorizedOnCodeLock(codeLock, player.userID))
            {
                if (_config.DebugLogging)
                    Puts($"{player.displayName} already authorized on code lock");
                return null;
            }

            int currentAuthCount = GetCodeLockAuthCount(codeLock);

            if (_config.DebugLogging)
                Puts($"CodeLock auth check: Current={currentAuthCount}, Max={_config.MaxAuthLimit}, Player={player.displayName}");

            if (currentAuthCount >= _config.MaxAuthLimit)
            {
                player.ChatMessage(string.Format(lang.GetMessage("AuthLimitReachedCodeLock", this, player.UserIDString), _config.MaxAuthLimit));

                var parentEntity = codeLock.GetParentEntity();
                ulong ownerId = codeLock.OwnerID != 0 ? codeLock.OwnerID : (parentEntity != null ? parentEntity.OwnerID : 0);

                SendAuthLimitWebhook(player, codeLock, "Code Lock", ownerId, currentAuthCount);

                if (_config.DebugLogging)
                    Puts($"Blocked code lock authorization for {player.displayName} - limit reached");

                return true;
            }

            return null;
        }

        #endregion

        #region Helpers

        private int GetCodeLockAuthCount(CodeLock codeLock)
        {
            int count = 0;

            if (_codeLockWhitelistField != null)
            {
                var whitelist = _codeLockWhitelistField.GetValue(codeLock) as List<ulong>;
                if (whitelist != null)
                    count += whitelist.Count;
            }

            if (_codeLockGuestlistField != null)
            {
                var guestlist = _codeLockGuestlistField.GetValue(codeLock) as List<ulong>;
                if (guestlist != null)
                    count += guestlist.Count;
            }

            return count;
        }

        private bool IsPlayerAuthorizedOnCodeLock(CodeLock codeLock, ulong playerId)
        {
            if (_codeLockWhitelistField != null)
            {
                var whitelist = _codeLockWhitelistField.GetValue(codeLock) as List<ulong>;
                if (whitelist != null && whitelist.Contains(playerId))
                    return true;
            }

            if (_codeLockGuestlistField != null)
            {
                var guestlist = _codeLockGuestlistField.GetValue(codeLock) as List<ulong>;
                if (guestlist != null && guestlist.Contains(playerId))
                    return true;
            }

            return false;
        }

        #endregion

        #region Admin Commands

        [ChatCommand("authlimit.check")]
        private void CmdCheckAuthLimit(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PermissionAdmin))
            {
                player.ChatMessage("You don't have permission to use this command.");
                return;
            }

            player.ChatMessage($"=== AuthLimit Info ===");
            player.ChatMessage($"Max Auth Limit: {_config.MaxAuthLimit}");
            player.ChatMessage($"TCs Limited: {_config.Features.LimitTCs}");
            player.ChatMessage($"Code Locks Limited: {_config.Features.LimitCodeLocks}");
            player.ChatMessage($"Turrets Limited: {_config.Features.LimitTurrets}");
            player.ChatMessage($"Discord Webhook: {(!string.IsNullOrEmpty(_config.Discord.WebhookUrl) ? "Configured" : "Not configured")}");
            if (!string.IsNullOrEmpty(_config.Discord.WebhookUrl))
            {
                player.ChatMessage($"Webhook Cooldown: {_config.Discord.WebhookCooldown} seconds");
            }
        }

        #endregion
    }
}
