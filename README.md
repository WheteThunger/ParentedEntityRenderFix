## How it works

This plugin fixes a client bug where some parented entities, especially electrical entities like switches and search lights, tend to not render visually unless the player is near the center of the map, even though the player will still see interaction prompts if they look in the right place.

Here are some example plugins that parent electrical entities that have this problem. Installing this plugin should mostly address the issue for their use cases.

- Car Turrets (switches)
- Drone Turrets (switches)
- MiniCopter Options (switches, search lights)
- WarCopter (switches, search lights)
- Bomb Trucks (RF receivers)
- Turrets Extended (switches)

Note: This plugin does not completely solve the problem for entities that move. However, players will be able to see them again by disconnecting and reconnecting, or by leaving the area and returning (until those entities move again).

## Performance warning

This plugin is optimized for performance, but it makes use of hooks that are called very frequently (there was no other way). This means that while the plugin will not cause lag spikes, it can slightly reduce the overall FPS of your server, depending on your player count, entity count, and frequency of players damaging entities or teleporting. This isn't avoidable. Please monitor the total hook time of the plugin on your server (seen next to the plugin info when you run `o.plugins`), as well as your server FPS, to determine if you should continue running this plugin.

If you want to determine if this plugin is causing (or will cause) a significant server FPS drain, you can safely unload and reload the plugin while monitoring your server FPS to see if there is a difference while loaded vs unloaded.

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
