// 命中/开火/爆炸闪光：程序化球体，出生后 0.2s 内缩小消失。
// 没有粒子资产，也没有 ParticleSystem——灰盒阶段特效只回答"哪里发生了什么"。

using System.Collections.Generic;
using UnityEngine;

namespace StarPawn.Presentation
{
    public class BattleFx : MonoBehaviour
    {
        private struct Item
        {
            public Transform Go;
            public float Age;
            public float Life;
            public float Size;
        }

        private readonly List<Item> _items = new List<Item>();

        /// <summary>在指定位置生成一枚闪光。size 单位 = km（与 SimMath 同口径）。</summary>
        public void Flash(Vector3 pos, Color color, float size, float life = 0.22f)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "Fx";
            go.transform.SetParent(transform, false);
            go.transform.position = pos;
            go.transform.localScale = Vector3.one * Mathf.Max(0.05f, size);
            Collider col = go.GetComponent<Collider>();
            if (col != null) col.enabled = false;
            MeshRenderer r = go.GetComponent<MeshRenderer>();
            if (r != null) r.material.color = color;

            Item item;
            item.Go = go.transform;
            item.Age = 0f;
            item.Life = life;
            item.Size = go.transform.localScale.x;
            _items.Add(item);
        }

        private void Update()
        {
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                Item it = _items[i];
                it.Age += Time.deltaTime;
                if (it.Age >= it.Life)
                {
                    Destroy(it.Go.gameObject);
                    _items.RemoveAt(i);
                    continue;
                }
                float k = 1f - it.Age / it.Life;
                it.Go.localScale = Vector3.one * it.Size * (0.4f + 0.6f * k);
            }
        }

        /// <summary>重开局时清场。</summary>
        public void ClearAll()
        {
            foreach (Item it in _items)
            {
                if (it.Go != null) Destroy(it.Go.gameObject);
            }
            _items.Clear();
        }
    }
}
