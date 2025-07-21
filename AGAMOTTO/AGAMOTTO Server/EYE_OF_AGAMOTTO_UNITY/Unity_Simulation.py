
from mlagents_envs.environment import UnityEnvironment

#update biuld location AFTER building
#Make sure to build the Unity environment first (File > Build Settings > Build) and point to it with --env.
env = UnityEnvironment(file_name="build/PushBlock")
env.reset()

behavior_name = list(env.behavior_specs)[0]
spec = env.behavior_specs[behavior_name]

for _ in range(1000):
    decision_steps, terminal_steps = env.get_steps(behavior_name)
    action = spec.action_spec.random_action(len(decision_steps))
    env.set_actions(behavior_name, action)
    env.step()
