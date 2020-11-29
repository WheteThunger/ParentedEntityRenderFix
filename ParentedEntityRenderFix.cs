using Network;
using Oxide.Core;
using System.Collections.Generic;
using System.IO;

namespace Oxide.Plugins
{
    [Info("Parented Entity Display Fix", "WhiteThunder", "0.1.0")]
    [Description("Fixes bug where some parented entities are not rendered.")]
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

        // This is used to keep track of which clients are aware of each entity
        // When we expect the client to destroy an entity, we update this state
        private readonly Dictionary<uint, HashSet<ulong>> entitySubscribers = new Dictionary<uint, HashSet<ulong>>();

        #endregion

        #region Hooks

        private void OnPlayerDisconnected(BasePlayer player)
        {
            foreach (var entry in entitySubscribers)
                entry.Value.Remove(player.userID);
        }

        private void OnEntityKill(IOEntity entity)
        {
            entitySubscribers.Remove(entity.net.ID);
        }

        // Intercept initial snapshot sent to client
        // Send an extra update ahead of it that alters the parent and position
        private void OnEntitySnapshot(IOEntity entity, Connection connection)
        {
            var parent = entity.GetParentEntity();
            if (parent == null)
                return;

            HashSet<ulong> playerList;
            if (!entitySubscribers.TryGetValue(entity.net.ID, out playerList))
            {
                playerList = new HashSet<ulong>();
                entitySubscribers[entity.net.ID] = playerList;
            }

            if (playerList.Contains(connection.ownerid))
                return;

            playerList.Add(connection.ownerid);
            SendSnapshot(entity, connection);
        }

        // Clients destroy entities from a network group when they leave it
        private void OnNetworkGroupLeft(BasePlayer player, Network.Visibility.Group group)
        {
            for (var i = 0; i < group.networkables.Count; i++)
            {
                var networkable = group.networkables.Values.Buffer[i];
                if (networkable == null)
                    continue;

                var entity = networkable.handler as BaseNetworkable;
                if (entity == null || entity.net == null)
                    continue;

                HashSet<ulong> playerList;
                if (entitySubscribers.TryGetValue(entity.net.ID, out playerList))
                    playerList.Remove(player.userID);
            }
        }

        #endregion

        #region Helper Methods

        // This is basically a copy of the Rust code for sending a snapshot
        // Examples: BasePlayer.SendEntitySnapshot(BaseNetworkable), BaseNetworkable.SendAsSnapshot(Connection)
        private void SendSnapshot(BaseNetworkable entity, Connection connection)
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
                ToStreamForNetwork(entity, Net.sv.write, saveInfo);
                Net.sv.write.Send(new SendInfo(connection));
            }
        }

        // This is basically a copy of the Rust code for streaming an entity to a network connection or cache
        private void ToStreamForNetwork(BaseNetworkable entity, Stream stream, BaseNetworkable.SaveInfo saveInfo)
        {
            using (saveInfo.msg = Facepunch.Pool.Get<ProtoBuf.Entity>())
            {
                entity.Save(saveInfo);
                Interface.CallHook("OnEntitySaved", this, saveInfo);
                MutateInitialSaveInfo(entity, saveInfo);
                saveInfo.msg.ToProto(stream);
                entity.PostSave(saveInfo);
            }
        }

        // Alter the entity's parentage, position and rotation so that the client determines the correct the world position
        private void MutateInitialSaveInfo(BaseNetworkable entity, BaseNetworkable.SaveInfo saveInfo)
        {
            var parent = entity.GetParentEntity();
            saveInfo.msg.baseEntity.pos = entity.transform.position;
            saveInfo.msg.baseEntity.rot = entity.transform.rotation.eulerAngles;
            saveInfo.msg.parent = null;
        }

        #endregion
    }
}
