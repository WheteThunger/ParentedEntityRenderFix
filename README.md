## How it works

This plugin attempts to work around a client bug where certain entities appear invisible when parented. For example, if you use the Modular Car Turrets plugin, which attaches an electric switch to an auto turret, you may notice that the switch is sometimes invisible, even though the player can still see interaction prompts like "turn on" when they are looking at the switch.

This plugin aims to fix this across the board, without requiring any changes to affected plugins.

### How the bug works

**Background:** Since some entities don't need to be rendered very far away, the game developers implemented logic to cull certain entities to save on rendering performance. The game client does this by measuring the distance between the player and the entity, and hiding the entity if it's beyond the max render distance, which depends on the type of entity. However, since most cullable entities are not expected to move in the vanilla game, the client evidently doesn't take into account the entity's current position. Instead, only the entity's original position is used for the distance measurement. By "original", I mean the position that the client received when it first became aware of the entity. Because it's client dependent, it's possible that for two different clients in the same position, one client will cull the entity, and the other may not, particularly if the entity moved.

**Bug:** When the game client becomes aware of an entity that is parented, instead of saving the entity's world position for the purpose of render culling, the client instead saves the entity's local position (position relative to the entity's parent). The local position coordinates are usually low since the entity is typically intended to be visually attached to its parent. This results in the entity only rendering if the client is near the center of the map (which is where position coordinates are lowest).

**Workaround:** This plugin attempts to work around the bug by detecting when an entity is being sent to a client for the first time, and then sending an additional snapshot beforehand. The additional snapshot is modified, indicating to the client that the entity is not actually parented, and that it is at the desired world position. This causes the client to save the correct world position as the entity's rendering origin. The default snapshot is then sent, which makes the client aware that the entity is actually parented, resuming normal function.

**Facepunch, if you are reading this:** Please fix this bug, so that this workaround will no longer be necessary. It should be pretty simple to fix. I spent an unreasonable amount of time fixing this bug for the community, and it isn't even viable for all servers due to the performance cost.

### Example entities affected by this bug

There are potentially many entities affected by this bug, so these are just examples.

- Electric switches
- RF receivers
- Search lights
- Small furnaces
- Tesla coils

Example plugins that parent above entities: Car Turrets, Bomb Trucks, Drone Lights, Drone Turrets.

## Performance warning

While this plugin's code is optimized for performance, this plugin subscribes to hooks that Oxide calls very frequently. Oxide has a significant overhead for these calls, which can cause a significant performance drop for servers with high population and high entity count, especially if players frequently spawn in or teleport to areas with many entities.

You may test out the performance cost of this plugin, while monitoring your server FPS before and after. You can also get a sense from the total hook time that Oxide reports next to the plugin (in seconds) when you run `o.plugins`. Note: A plugin's total hook time is expected to start at 0 when a plugin loads, and it goes up over time as the plugin gradually uses hooks. If that number climbs quickly (e.g., 1 second per minute) then it means the plugin may be using a significant percentage of your overall performance budget.

## Installation

1. Add the plugin to the `oxide/plugins` directory of your Rust server installation
2. Update the plugin configuration to include the short prefab names of entities that are affected on your server (depends on which plugins you have installed)
3. Reload the plugin
4. If you already have parented entities in your vicitinity that are invisible, you will have to leave the area and return for them to reappear for your client.

## Configuration

Default configuration (does nothing):

```json
{
  "EnabledEntities": []
}
```

- `EnabledEntities` -- This list of entity short prefab names determines which entity types the render fix will be applied to. Keeping this list shorter helps with performance. Only add entities that are being parented by plugins on your server.

Example config:

```json
{
  "EnabledEntities": [
    "rfreceiver",
    "searchlight.deployed",
    "switch"
  ]
}
```

## FAQ

### What about entities that move?

This plugin does not directly address entities moving. This means that an entity may appear invisible after moving some distance from its spawn origin. However, this plugin's logic will partially mitigate this issue. That is, any clients that subscribe to an entity after it has moved should render it accurately until the entity moves again.

It is also possible to periodically send messages to clients to terminate and recreate entities that have moved. This would have the effect of updating the client's cached rendering origin for the entity. This may be implemented in a future update.

### I'm a plugin developer, what can I do to reduce the impact of this issue?

You can instruct your users to install this plugin, and to configure it to be aware of the entity types that your plugin parents. Be sure to verify that those entity types are affected first. In the future, this plugin may have an API that allows your plugin to register those entity types automatically, to save your users from having to configure them.

Since this plugin has a significant performance cost, it is not appropriate for all servers, so you should also try to reduce the impact of the parenting bug in your plugins. A lightweight way to do so is to spawn the entity unparented at the desired world position, and then parent it after. This will render the entity accurately for clients who observed the entity spawning. However, if one of those clients leaves the area (or server) and returns, the entity will likely be invisible, since that technically destroys and recreates the entity client-side, which flushes the client's cache of the entity's rendering origin.
