using System;
using UnityEngine;

namespace ShopAssist
{
    internal sealed class ShopLoop : MonoBehaviour
    {
        internal Action Tick;
        private void LateUpdate() => Tick?.Invoke();
    }
}
