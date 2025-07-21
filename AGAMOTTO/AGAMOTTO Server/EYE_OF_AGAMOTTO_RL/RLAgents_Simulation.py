import gymnasium as gym
import numpy as np
import ale_py
import torch
import matplotlib.pyplot as plt
import torch.nn as nn
import torch.optim as optim
import torch.nn.functional as F
import math
import random
import matplotlib
from collections import namedtuple, deque
from itertools import count
import time
from tqdm import tqdm


is_ipython = 'inline' in matplotlib.get_backend()
if is_ipython:
    from IPython import display

from EYE_OF_AGAMOTTO_RL.Agents import CommandAgent

commandAgent = CommandAgent()

# Plotting function
def plot_durations(reward_list, show_result=False):
    plt.figure(1)
    plt.clf()


    if show_result:
        plt.title('Result')
    else:
        plt.title('Training...')

    plt.xlabel('Episode')
    plt.ylabel('Reward')
    plt.plot(reward_list, label=f"Agent")

    plt.legend()
    plt.pause(0.001)
    if is_ipython:
        if not show_result:
            display.display(plt.gcf())
            display.clear_output(wait=True)
        else:
            display.display(plt.gcf())

#commandAgent.train("ALE/Adventure-v5", 5000)
commandAgent.train("ALE/AirRaid-v5", 5000)
