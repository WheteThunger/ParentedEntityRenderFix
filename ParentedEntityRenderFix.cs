using Network;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Oxide.Core;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Oxide.Plugins
{
    [Info("Parented Entity Render Fix", "WhiteThunder", "0.1.0")]
    [Description("Fixes bug where some parented entities do not render except near map origin.")]
    /**
     * ## Background
     *
     * For some entities, especially IO entities, the Rust client will cache the initial network
     * position (entity.transform.localPosition) as the rendering origin. When the player gets a
     * sufficient distance away from that rendering origin, the entity will not be rendered,
     * presumably as a performance optimization. However, there is a bug where the client does
     * not take into account that the entity was parented, even though that information was sent
     * in the same snapshot. Since most plugins set localPosition to a relatively small vector,
     * this causes these entities to not render unless the player is near the map origin.
     *
     * ## Previously attempted workarounds
     *
     * Some developers have attempted to work around this issue by spawning the entity unparented
     * at the desired world position. This works because the networked position (localPosition) is
     * equal to the world position, so the Rust client correctly caches that as the rendering
     * origin. However, you then have to solve the problem of manually destroying the entity when
     * its intended parent is destroyed, not to mention move the entity when its parent moves.
     *
     * Some have tried to avoid manual position updates by simply parenting the entity after it is
     * spawned with the correct world position. This appears to work at first because only the
     * initial snapshot needs to send the world position. Subsequent updates can indicate parentage
     * and localPosition without issue. However, this only works for clients that were networked
     * the original position. Clients not subscribed to the entity's network group at the time it
     * spawned, or clients who disconnect or leave that entity's network group (which causes the
     * entity to be destroyed client-side), will reproduce the problem the next time they create
     * the entity since it will be parented in the initial snapshot they receive.
     *
     * Neither of those workarounds solve the problem of an entity moving signifcantly away from
     * its rendering origin. To solve that, the entity must be periodically destroyed and recreated
     * client-side (does not have to happen server-side).
     *
     * ## How this plugin works
     *
     * This plugin solves the problem by sending an additional network update ahead of the update
     * that would cause the client to create the entity. This extra update essentially lies about
     * the entity's parentage and network position, causing the client to cache the correct
     * rendering origin. This plugin is able to deduce that a client is creating or destroying an
     * entity by using multiple hooks and keeping state.
     *
     * For entities that move a significant distance from their spawn location, players that move
     * with the entity will not be able to see it, but disconnecting or leaving the network group
     * and returning will recreate the entity client-side which will resolve the issue.
     */
    internal class ParentedEntityRenderFix : CovalencePlugin
    {
        #region Fields

        private static Configuration _pluginConfig;

        #endregion

        #region Hooks

        private void OnPlayerDisconnected(BasePlayer player)
        {
            if (player.IsNpc || !player.userID.IsSteamId())
                return;

            EntitySubscriptionManager.Instance.RemoveSubscriber(player.userID);
        }

        private void OnEntityKill(BaseEntity entity)
        {
            if (entity == null || entity.net == null)
                return;

            EntitySubscriptionManager.Instance.RemoveEntity(entity.net.ID);
            NetworkCacheManager.Instance.InvalidateForEntity(entity.net.ID);
        }

        private void OnEntitySnapshot(BaseEntity entity, Connection connection)
        {
            if (entity == null || entity.net == null)
                return;

            if (!entity.HasParent())
                return;

            if (!_pluginConfig.EnabledEntities.Contains(entity.ShortPrefabName))
                return;

            // Detect when the vanilla network cache has been cleared in order to invalidate the custom cache.
            if (entity._NetworkCache == null)
                NetworkCacheManager.Instance.InvalidateForEntity(entity.net.ID);

            // Ignore if the subscription was already being tracked, which indicates the client already has the entity.
            if (EntitySubscriptionManager.Instance.AddEntitySubscription(entity.net.ID, connection.ownerid))
            {
                // Send an extra update ahead of the original, to represent the entity as unparented.
                NetworkCacheManager.Instance.SendModifiedSnapshot(entity, connection);
            }
        }

        // Clients destroy entities from a network group when the client leaves it.
        private void OnNetworkGroupLeft(BasePlayer player, Network.Visibility.Group group)
        {
            if (player.IsNpc || !player.userID.IsSteamId())
                return;

            for (var i = 0; i < group.networkables.Count; i++)
            {
                var networkable = group.networkables.Values.Buffer[i];
                if (networkable == null)
                    continue;

                var entity = networkable.handler as BaseNetworkable;
                if (entity == null || entity.net == null)
                    continue;

                EntitySubscriptionManager.Instance.RemoveEntitySubscription(entity.net.ID, player.userID);
            }
        }

        #endregion

        #region Network Cache Manager

        private abstract class BaseNetworkCacheManager
        {
            private readonly Dictionary<uint, Stream> _networkCache = new Dictionary<uint, Stream>();

            public void InvalidateForEntity(uint entityId)
            {
                _networkCache.Remove(entityId);
            }

            // Mostly copied from:
            // - `BaseNetworkable.SendAsSnapshot(Connection)`
            // - `BasePlayer.SendEntitySnapshot(BaseNetworkable)`
            public void SendModifiedSnapshot(BaseEntity entity, Connection connection)
            {
                if (Net.sv.write.Start())
                {
                    connection.validate.entityUpdates++;
                    var saveInfo = new BaseNetworkable.SaveInfo()
                    {
                        forConnection = connection,
                        forDisk = false
                    };
                    Net.sv.write.PacketID(Message.Type.Entities);
                    Net.sv.write.UInt32(connection.validate.entityUpdates);
                    ToStream(entity, Net.sv.write, saveInfo);
                    Net.sv.write.Send(new SendInfo(connection));
                }
            }

            // Mostly copied from `BaseNetworkable.ToStream(Stream, SaveInfo)`.
            private void ToStream(BaseEntity entity, Stream stream, BaseNetworkable.SaveInfo saveInfo)
            {
                using (saveInfo.msg = Facepunch.Pool.Get<ProtoBuf.Entity>())
                {
                    entity.Save(saveInfo);
                    Interface.CallHook("OnEntitySaved", entity, saveInfo);
                    HandleOnEntitySaved(entity, saveInfo);
                    saveInfo.msg.ToProto(stream);
                    entity.PostSave(saveInfo);
                }
            }

            // Mostly copied from `BaseNetworkable.ToStreamForNetwork(Stream, SaveInfo)`.
            private Stream ToStreamForNetwork(BaseEntity entity, BaseNetworkable.SaveInfo saveInfo)
            {
                // Check entity network cache. If empty, we assume our cache is dirty too.
                Stream cachedStream;
                if (_networkCache.TryGetValue(entity.net.ID, out cachedStream))
                    return cachedStream;

                cachedStream = BaseNetworkable.EntityMemoryStreamPool.Count > 0
                    ? BaseNetworkable.EntityMemoryStreamPool.Dequeue()
                    : new MemoryStream(8);

                ToStream(entity, cachedStream, saveInfo);
                _networkCache[entity.net.ID] = cachedStream;

                return cachedStream;
            }

            // Handler for modifying save info when building a snapshot.
            protected abstract void HandleOnEntitySaved(BaseEntity entity, BaseNetworkable.SaveInfo saveInfo);
        }

        private class NetworkCacheManager : BaseNetworkCacheManager
        {
            private static NetworkCacheManager _instance = new NetworkCacheManager();
            public static NetworkCacheManager Instance => _instance;

            protected override void HandleOnEntitySaved(BaseEntity entity, BaseNetworkable.SaveInfo saveInfo)
            {
                var parent = entity.GetParentEntity();
                saveInfo.msg.baseEntity.pos = entity.transform.position;
                saveInfo.msg.baseEntity.rot = entity.transform.rotation.eulerAngles;
                saveInfo.msg.parent = null;
            }
        }

        #endregion

        #region Entity Subscriber Manager

        private class EntitySubscriptionManager
        {
            private static EntitySubscriptionManager _instance = new EntitySubscriptionManager();
            public static EntitySubscriptionManager Instance => _instance;

            private readonly Dictionary<uint, HashSet<ulong>> _entitySubscibers = new Dictionary<uint, HashSet<ulong>>();

            public bool AddEntitySubscription(uint entityId, ulong userId)
            {
                HashSet<ulong> subscribers;
                if (_entitySubscibers.TryGetValue(entityId, out subscribers))
                    return subscribers.Add(userId);

                _entitySubscibers[entityId] = new HashSet<ulong>() { userId };
                return true;
            }

            public void RemoveEntitySubscription(uint entityId, ulong userId)
            {
                HashSet<ulong> subscribers;
                if (_entitySubscibers.TryGetValue(entityId, out subscribers))
                    subscribers.Remove(userId);
            }

            public void RemoveSubscriber(ulong userId)
            {
                foreach (var entry in _entitySubscibers)
                    entry.Value.Remove(userId);
            }

            public void RemoveEntity(uint entityId)
            {
                _entitySubscibers.Remove(entityId);
            }
        }

        #endregion

        #region Configuration

        private class Configuration : SerializableConfiguration
        {
            [JsonProperty("EnabledEntities")]
            public HashSet<string> EnabledEntities = new HashSet<string>();
        }

        private Configuration GetDefaultConfig() => new Configuration();

        #endregion

        #region Configuration Boilerplate

        private class SerializableConfiguration
        {
            public string ToJson() => JsonConvert.SerializeObject(this);

            public Dictionary<string, object> ToDictionary() => JsonHelper.Deserialize(ToJson()) as Dictionary<string, object>;
        }

        private static class JsonHelper
        {
            public static object Deserialize(string json) => ToObject(JToken.Parse(json));

            private static object ToObject(JToken token)
            {
                switch (token.Type)
                {
                    case JTokenType.Object:
                        return token.Children<JProperty>()
                                    .ToDictionary(prop => prop.Name,
                                                  prop => ToObject(prop.Value));

                    case JTokenType.Array:
                        return token.Select(ToObject).ToList();

                    default:
                        return ((JValue)token).Value;
                }
            }
        }

        private bool MaybeUpdateConfig(SerializableConfiguration config)
        {
            var currentWithDefaults = config.ToDictionary();
            var currentRaw = Config.ToDictionary(x => x.Key, x => x.Value);
            return MaybeUpdateConfigDict(currentWithDefaults, currentRaw);
        }

        private bool MaybeUpdateConfigDict(Dictionary<string, object> currentWithDefaults, Dictionary<string, object> currentRaw)
        {
            bool changed = false;

            foreach (var key in currentWithDefaults.Keys)
            {
                object currentRawValue;
                if (currentRaw.TryGetValue(key, out currentRawValue))
                {
                    var defaultDictValue = currentWithDefaults[key] as Dictionary<string, object>;
                    var currentDictValue = currentRawValue as Dictionary<string, object>;

                    if (defaultDictValue != null)
                    {
                        if (currentDictValue == null)
                        {
                            currentRaw[key] = currentWithDefaults[key];
                            changed = true;
                        }
                        else if (MaybeUpdateConfigDict(defaultDictValue, currentDictValue))
                            changed = true;
                    }
                }
                else
                {
                    currentRaw[key] = currentWithDefaults[key];
                    changed = true;
                }
            }

            return changed;
        }

        protected override void LoadDefaultConfig() => _pluginConfig = GetDefaultConfig();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _pluginConfig = Config.ReadObject<Configuration>();
                if (_pluginConfig == null)
                {
                    throw new JsonException();
                }

                if (MaybeUpdateConfig(_pluginConfig))
                {
                    LogWarning("Configuration appears to be outdated; updating and saving");
                    SaveConfig();
                }
            }
            catch
            {
                LogWarning($"Configuration file {Name}.json is invalid; using defaults");
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig()
        {
            Log($"Configuration changes saved to {Name}.json");
            Config.WriteObject(_pluginConfig, true);
        }

        #endregion

    }
}
