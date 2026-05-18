# AGAMOTTO Unity Simulation Setup

## 1. Prerequisites

### Install Unity Editor
- **Version:** `2022.3.62f3` (see `ProjectSettings/ProjectVersion.txt`)
- **Modules:**
  - Android Build Support  
  - Windows Build Support

---

## 2. Project Setup

### Step 1. Download RIDE Package
Download `Ride.zip`, which contains all RIDE assets and dependencies

### Step 2. Copy RIDE Assets
Extract and copy the following folders into your AGAMOTTO project:

| Source | Destination |
|--------|--------------|
| `ride/RideUnity/Assets/Ride` | `../AGAMOTTO/AGAMOTTO Simulation/Assets/Ride` |
| `ride/RideUnity/Assets/Ride_Art` | `../AGAMOTTO/AGAMOTTO Simulation/Assets/Ride_Art` |
| `ride/RideUnity/Assets/Ride_Dependencies` | `../AGAMOTTO/AGAMOTTO Simulation/Assets/Ride_Dependencies` |

---

## 3. Unity Package Manager Dependencies

In Unity Editor, open **Window -> Package Manager**.  
Click **+ (plus icon)** -> **Add package by name** and add each of the following:

```text
com.unity.nuget.newtonsoft-json
com.unity.ml-agents
com.unity.ugui
com.unity.textmeshpro
com.unity.inputsystem
com.unity.timeline
com.unity.ai.navigation
com.unity.editorcoroutines
```

---

## 4. Photon Networking Setup

### Install Photon PUN 2
- **URL:** [PUN 2 Free (Asset Store)](https://assetstore.unity.com/packages/tools/network/pun-2-free-119922?srsltid=AfmBOopOozMJcT4_zWWHNv1Mpf3GSeP39uqokbB0jXHUN3ssG4y9BtP5)
- Add to **My Assets** -> **Open in Unity Editor** -> **Download** -> **Import**

---

## 5. UnityMainThreadDispatcher Integration

### Download Required Files
From [PimDeWitte’s GitHub Repository](https://github.com/PimDeWitte/UnityMainThreadDispatcher):

- **Script:** [`UnityMainThreadDispatcher.cs`](https://github.com/PimDeWitte/UnityMainThreadDispatcher/blob/master/Runtime/UnityMainThreadDispatcher.cs)
  - Place in: `Assets/Scripts/`

- **Prefab:** [`UnityMainThreadDispatcher.prefab`](https://github.com/PimDeWitte/UnityMainThreadDispatcher/blob/master/Runtime/UnityMainThreadDispatcher.prefab)
  - Place in: `Assets/Prefabs/`

---

## 6. Optional Fix (PhotonChat Demo Error)

To fix:
```
Assets\Ride_Dependencies\Photon\PhotonChat\Demos\DemoChat\ChatGui.cs(473,41): 
error CS0246: The type or namespace name 'ChannelSelector' could not be found 
(are you missing a using directive or an assembly reference?)
```

Delete the following directory:
```
Assets/Ride_Dependencies/Photon/PhotonChat/Demos
```

---

## 7. Running the Simulation

### Open Scene
Navigate to:
```
Assets/Scenes/BaseScene
```

### Add Dispatcher Prefab
Drag and drop the `UnityMainThreadDispatcher` prefab into the scene, then **save**.

### Configure Terrain Key
In Unity:
```
Ride > Terrain > Config File - Terrain Key
```

Use the following key:

```text
us-west-2:411c00cd-90ec-4540-bf1e-934504e5f918
```

---

**The simulation environment should now be ready to run!**
