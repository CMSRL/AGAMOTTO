using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

public class WaitHelper : MonoBehaviour
{
    public void WaitUntil(Func<bool> condition, Action onComplete, float checkInterval = 0.1f)
    {
        StartCoroutine(WaitUntilCoroutine(condition, onComplete, checkInterval));
    }

    private IEnumerator WaitUntilCoroutine(Func<bool> condition, Action onComplete, float checkInterval)


    {
        while (!condition())
        {
            yield return new WaitForSeconds(checkInterval);
        }

        onComplete?.Invoke();
    }
}


