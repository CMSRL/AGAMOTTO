using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using Ride;

namespace NewProjectTemplate
{
    class LevelSelect : RideMonoBehaviour
    {
        [SerializeField] DebugMenu m_debugMenu;
        [SerializeField] string m_uncategorizedName = null;

        [Serializable]
        class SectionInfo
        {
            public string sectionName = null;
            public string [] scenes = null;
        }
        [SerializeField] SectionInfo [] m_sections = null;


        Vector2 m_levelScrollPosition;

        AsyncOperation m_loadingLevelStatus = null;


        protected override void Start()
        {
            base.Start();

            m_debugMenu.SetMenuSize(0.1f, 0.04f, 0.8f, 0.92f);
            m_debugMenu.AddDebugMenuFront("Level Select", OnGUILevelSelect);
            m_debugMenu.ShowMenu(true);
        }


        protected override void Update()
        {
            base.Update();

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                VHUtils.ApplicationQuit();
            }
        }


        void OnGUILevelSelect()
        {
            if (m_loadingLevelStatus == null)
            {
                using (var scrollViewScope = new GUILayout.ScrollViewScope(m_levelScrollPosition))
                {
                    m_levelScrollPosition = scrollViewScope.scrollPosition;

                    Dictionary<string, bool> scenes = new Dictionary<string, bool>();
                    for (int i = 0; i < SceneManager.sceneCountInBuildSettings; i++)
                    {
                        var path = SceneUtility.GetScenePathByBuildIndex(i);
                        var scene = System.IO.Path.GetFileNameWithoutExtension(path);
                        scenes.Add(scene, false);
                    }

                    scenes["LevelSelect"] = true;

                    foreach (var section in m_sections)
                    {
                        GUILayout.Label(section.sectionName, m_debugMenu.GuiLabelStyle);

                        foreach (var scene in section.scenes)
                        {
                            if (scenes.ContainsKey(scene))
                            {
                                if (!scenes[scene])
                                {
                                    if (GUILayout.Button(scene, m_debugMenu.GuiButtonStyle))
                                    {
                                        m_loadingLevelStatus = SceneManager.LoadSceneAsync(scene);
                                    }

                                    // mark it as used
                                    scenes[scene] = true;
                                }
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(m_uncategorizedName))
                    {
                        GUILayout.Space(m_debugMenu.SpaceHeight * 2);
                    }
                    else
                    {
                        GUILayout.Label(m_uncategorizedName, m_debugMenu.GuiLabelStyle);
                    }

                    foreach (var scene in scenes)
                    {
                        if (!scene.Value)
                        {
                            if (GUILayout.Button(scene.Key, m_debugMenu.GuiButtonStyle))
                            {
                                m_loadingLevelStatus = SceneManager.LoadSceneAsync(scene.Key);
                            }
                        }
                    }
                }
            }
            else
            {
                GUILayout.HorizontalSlider(m_loadingLevelStatus.progress, 0, 1, m_debugMenu.GuiSliderStyle, m_debugMenu.GuiSliderThumbStyle);
            }
        }
    }
}
