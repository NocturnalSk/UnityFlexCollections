using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace UnityFlexCollections
{
    /// <summary>
    /// 增强版智能动态列表 - 针对小数据集优化
    /// 集成查询、排序、高效清空等功能
    /// </summary>
    public sealed class FlexList<T> : IEnumerable<T>, IList<T>
    {
        #region 核心数据结构

        private T[] _buffer;
        private int _count;
        private int _version;
        private readonly float _growthFactor;
        private readonly int _minGrow;
        private Stack<int> _freeSlots = new();
        private IComparer<T> _comparer;

        // 定义最大数组长度常量
        private const int MaxArrayLength = 0X7FFFFFC7; // .NET 中数组的最大长度2,147,483,591=int.MaxValue - 56

        #endregion

        #region 初始化配置

        public FlexList(int initialCapacity = 16,
            float growthFactor = 1.4f,
            int minGrow = 4,
            IComparer<T> comparer = null)
        {
            if (initialCapacity < 4) initialCapacity = 4;
            if (growthFactor < 1.1f) growthFactor = 1.1f;
            if (minGrow < 1) minGrow = 1;

            _buffer = new T[initialCapacity];
            _growthFactor = growthFactor;
            _minGrow = minGrow;
            _comparer = comparer ?? Comparer<T>.Default;
        }

        #endregion

        #region 基本操作

        /// <summary>
        /// 添加元素到列表
        /// </summary>
        public void Add(T item)
        {
            // 优先使用空闲槽位
            if (_freeSlots.Count > 0)
            {
                int index = _freeSlots.Pop();
                _buffer[index] = item;
                _version++;
                return;
            }

            // 检查是否需要扩容
            if (_count == _buffer.Length)
            {
                SmartGrow();
            }

            _buffer[_count] = item;
            _count++;
            _version++;
        }

        public void Clear()
        {
            Clear(ClearMode.Auto);
        }

        public bool Contains(T item)
        {
            var results = new List<T>();
            FindAll(x => EqualityComparer<T>.Default.Equals(x, item), results);
            return results.Count > 0;
        }


        public void CopyTo(T[] array, int arrayIndex)
        {
            if (array == null) throw new ArgumentNullException(nameof(array));
            if (arrayIndex < 0) throw new ArgumentOutOfRangeException(nameof(arrayIndex));
            if (array.Length - arrayIndex < _count) throw new ArgumentException("数组长度不足以容纳全部元素");

            for (int i = 0; i < _count; i++)
            {
                if (!_freeSlots.Contains(i))
                {
                    array[arrayIndex++] = _buffer[i];
                }
            }
        }

        /// <summary>
        /// 标记指定索引的元素为已删除
        /// </summary>
        public void MarkDeleted(int index)
        {
            if (index < 0 || index >= _count)
                throw new IndexOutOfRangeException();

            // 标记为默认值并记录空闲位置
            _buffer[index] = default;
            _freeSlots.Push(index);
            _version++;
        }

        /// <summary>
        /// 压缩列表，移除所有已标记删除的元素
        /// </summary>
        public void Compact()
        {
            if (_freeSlots.Count == 0) return;

            var newBuffer = new T[_buffer.Length];
            int newIndex = 0;

            // 重建连续内存块
            for (int i = 0; i < _count; i++)
            {
                if (!_freeSlots.Contains(i))
                {
                    newBuffer[newIndex++] = _buffer[i];
                }
            }

            _buffer = newBuffer;
            _count = newIndex;
            _freeSlots.Clear();
            _version++;
        }

        /// <summary>
        /// 每帧清理部分已删除元素（避免卡顿）
        /// </summary>
        /// <param name="maxProcessPerFrame">每帧最多处理的元素数量</param>
        public void FrameCleanup(int maxProcessPerFrame = 4)
        {
            if (_freeSlots.Count == 0) return;

            var processCount = Math.Min(maxProcessPerFrame, _freeSlots.Count);
            for (int i = 0; i < processCount; i++)
            {
                var freeIndex = _freeSlots.Pop();
                var lastIndex = _count - 1;

                // 将最后一个元素移动到空闲位置
                if (freeIndex != lastIndex)
                {
                    _buffer[freeIndex] = _buffer[lastIndex];

                    // 如果最后一个元素的位置在空闲列表中，更新它
                    if (_freeSlots.Contains(lastIndex))
                    {
                        // 创建新栈来更新位置
                        var newStack = new Stack<int>();
                        var tempArray = _freeSlots.ToArray();

                        foreach (var idx in tempArray)
                        {
                            newStack.Push(idx == lastIndex ? freeIndex : idx); // 替换为新的空闲位置
                        }

                        _freeSlots = newStack;
                    }
                }

                // 清除最后一个位置
                _buffer[lastIndex] = default;
                _count--;
            }

            _version++;
        }

        #endregion

        #region 清空功能增强

        /// <summary>
        /// 清空列表（自动选择最优策略）
        /// </summary>
        /// <param name="mode">0=自动选择, 1=立即清空, 2=标记删除, 3=彻底释放</param>
        public void Clear(ClearMode mode = ClearMode.Auto)
        {
            switch (mode)
            {
                case ClearMode.Immediate:
                    ClearImmediate();
                    break;
                case ClearMode.MarkDeleted:
                    MarkAllForDeletion();
                    break;
                case ClearMode.ReleaseMemory:
                    ClearAndRelease();
                    break;
                default: // Auto mode
                    if (_count <= 200)
                        ClearImmediate();
                    else
                        MarkAllForDeletion();
                    break;
            }
        }

        private void ClearImmediate()
        {
            // 小列表直接重置
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                for (int i = 0; i < _count; i++)
                {
                    if (!_freeSlots.Contains(i))
                    {
                        _buffer[i] = default;
                    }
                }
            }

            _count = 0;
            _freeSlots.Clear();
            _version++;
        }

        private void MarkAllForDeletion()
        {
            // 只标记有效元素
            for (int i = 0; i < _count; i++)
            {
                if (!_freeSlots.Contains(i))
                {
                    _freeSlots.Push(i);
                }
            }

            _version++;
        }

        private void ClearAndRelease()
        {
            _count = 0;
            _freeSlots.Clear();
            _buffer = new T[Math.Max(4, _minGrow)];
            _version++;
        }

        #endregion

        #region 高效查询功能

        /// <summary>
        /// 查找第一个匹配元素（高效实现）
        /// </summary>
        public T Find(Predicate<T> match)
        {
            // 小数据集直接遍历
            if (_count <= 50)
            {
                for (int i = 0; i < _count; i++)
                {
                    if (!_freeSlots.Contains(i) && match(_buffer[i]))
                        return _buffer[i];
                }

                return default;
            }

            // 使用迭代器（避免重复检查）
            using var enumerator = GetEnumerator();
            while (enumerator.MoveNext())
            {
                if (match(enumerator.Current))
                    return enumerator.Current;
            }

            return default;
        }

        /// <summary>
        /// 查找所有匹配元素（避免中间分配）
        /// </summary>
        public void FindAll(Predicate<T> match, List<T> results)
        {
            if (results == null)
                throw new ArgumentNullException(nameof(results));

            results.Clear();

            if (_count <= 30)
            {
                // 小数据集直接遍历
                for (int i = 0; i < _count; i++)
                {
                    if (!_freeSlots.Contains(i) && match(_buffer[i]))
                        results.Add(_buffer[i]);
                }

                return;
            }

            // 使用迭代器
            using var enumerator = GetEnumerator();
            while (enumerator.MoveNext())
            {
                if (match(enumerator.Current))
                    results.Add(enumerator.Current);
            }
        }

        /// <summary>
        /// 检查是否存在匹配元素（提前退出优化）
        /// </summary>
        public bool Exists(Predicate<T> match)
        {
            // 小数据集直接遍历
            if (_count <= 40)
            {
                for (int i = 0; i < _count; i++)
                {
                    if (!_freeSlots.Contains(i) && match(_buffer[i]))
                        return true;
                }

                return false;
            }

            // 使用迭代器
            var enumerator = GetEnumerator();
            while (enumerator.MoveNext())
            {
                if (match(enumerator.Current))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 查找元素索引（高效实现）
        /// </summary>
        public int IndexOf(T item)
        {
            var comparer = EqualityComparer<T>.Default;

            // 小数据集直接遍历
            if (_count <= 60)
            {
                for (var i = 0; i < _count; i++)
                {
                    if (!_freeSlots.Contains(i) && comparer.Equals(_buffer[i], item))
                        return i;
                }

                return -1;
            }

            // 使用迭代器
            int index = 0;
            using var enumerator = GetEnumerator();
            while (enumerator.MoveNext())
            {
                if (comparer.Equals(enumerator.Current, item))
                    return index;
                index++;
            }

            return -1;
        }

        public void Insert(int index, T item)
        {
            if (index < 0 || index > _count)
                throw new ArgumentOutOfRangeException(nameof(index));

            Compact();

            if (_count == _buffer.Length)
                SmartGrow();

            if (index < _count)
            {
                Array.Copy(_buffer, index, _buffer, index + 1, _count - index);
            }

            _buffer[index] = item;
            _count++;
            _version++;
        }


        #endregion

        #region 排序功能增强

        /// <summary>
        /// 高效排序（使用内联比较）
        /// </summary>
        public void Sort()
        {
            Compact(); // 排序前先压缩
            Array.Sort(_buffer, 0, _count, _comparer);
            _version++;
        }

        /// <summary>
        /// 带自定义比较器的排序
        /// </summary>
        public void Sort(IComparer<T> comparer)
        {
            if (comparer == null) throw new ArgumentNullException(nameof(comparer));

            Compact(); // 排序前先压缩
            Array.Sort(_buffer, 0, _count, comparer);
            _version++;
        }

        /// <summary>
        /// 带比较委托的排序
        /// </summary>
        public void Sort(Comparison<T> comparison)
        {
            if (comparison == null) throw new ArgumentNullException(nameof(comparison));

            Compact(); // 排序前先压缩
            Array.Sort(_buffer, 0, _count, Comparer<T>.Create(comparison));
            _version++;
        }

        /// <summary>
        /// 部分排序（排序指定范围内的元素）
        /// </summary>
        public void Sort(int index, int count, IComparer<T> comparer = null)
        {
            if (index < 0 || count < 0 || index + count > _count)
                throw new ArgumentOutOfRangeException();

            Compact(); // 排序前先压缩
            Array.Sort(_buffer, index, count, comparer ?? _comparer);
            _version++;
        }

        #endregion

        #region 扩容策略

        /// <summary>
        /// 智能扩容算法（核心优化）
        /// </summary>
        private void SmartGrow()
        {
            int newCapacity;

            // 动态计算扩容大小
            if (_buffer.Length < 64)
            {
                // 小列表使用固定步长
                newCapacity = _buffer.Length + _minGrow * 2;
            }
            else
            {
                // 大列表使用渐进式增长
                float grow = _buffer.Length * (_growthFactor - 1);
                newCapacity = _buffer.Length + Math.Max(
                    (int)grow,
                    _minGrow
                );
            }

            // 确保不超过最大数组长度
            if (newCapacity > MaxArrayLength)
            {
                newCapacity = MaxArrayLength;
            }

            // 应用新容量
            var newBuffer = new T[newCapacity];
            Array.Copy(_buffer, 0, newBuffer, 0, _count);
            _buffer = newBuffer;
        }

        #endregion

        #region 新增实用方法

        /// <summary>
        /// 转换为数组（避免额外分配）
        /// </summary>
        public T[] ToArray()
        {
            Compact();
            T[] result = new T[_count];
            Array.Copy(_buffer, 0, result, 0, _count);
            return result;
        }

        /// <summary>
        /// 高效添加范围
        /// </summary>
        public void AddRange(IEnumerable<T> collection)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            if (collection is ICollection<T> c)
            {
                int count = c.Count;
                EnsureCapacity(_count + count);

                if (c is T[] array)
                {
                    Array.Copy(array, 0, _buffer, _count, count);
                    _count += count;
                }
                else
                {
                    c.CopyTo(_buffer, _count);
                    _count += count;
                }

                _version++;
            }
            else
            {
                foreach (var item in collection)
                {
                    Add(item);
                }
            }
        }

        /// <summary>
        /// 移除指定元素（高效实现）
        /// </summary>
        public bool Remove(T item)
        {
            int index = IndexOf(item);
            if (index >= 0)
            {
                RemoveAt(index);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 移除指定索引元素
        /// </summary>
        public void RemoveAt(int index)
        {
            if (index < 0 || index >= _count)
                throw new IndexOutOfRangeException();

            if (_count <= 100)
            {
                // 小数据集直接移动元素
                _count--;
                if (index < _count)
                {
                    Array.Copy(_buffer, index + 1, _buffer, index, _count - index);
                }

                _buffer[_count] = default;
            }
            else
            {
                // 大数据集使用标记删除
                MarkDeleted(index);
            }

            _version++;
        }

        #endregion

        #region IEnumerable 实现

        public IEnumerator<T> GetEnumerator()
        {
            return new FlexEnumerator(this);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        private struct FlexEnumerator : IEnumerator<T>
        {
            private readonly FlexList<T> _list;
            private readonly int _version;
            private int _index;
            private T _current;

            public FlexEnumerator(FlexList<T> list)
            {
                _list = list;
                _version = list._version;
                _index = -1; // 从-1开始，在MoveNext中前进到第一个有效元素
                _current = default;
            }

            public bool MoveNext()
            {
                if (_version != _list._version)
                    throw new InvalidOperationException("Collection modified");

                // 跳过已删除元素
                while (true)
                {
                    _index++;
                    if (_index >= _list._count)
                        return false;

                    if (!_list._freeSlots.Contains(_index))
                    {
                        _current = _list._buffer[_index];
                        return true;
                    }
                }
            }

            public T Current => _current;
            object IEnumerator.Current => Current;

            public void Reset()
            {
                _index = -1;
                _current = default;
            }

            public void Dispose()
            {
            }
        }

        #endregion

        #region 辅助方法和属性

        public int Count => _count - _freeSlots.Count;
        public bool IsReadOnly { get; }
        public int Capacity => _buffer.Length;
        public int ActualCount => _count;

        public bool IsSlotMarkedDeleted(int index) => _freeSlots.Contains(index);

        private void EnsureCapacity(int min)
        {
            if (_buffer.Length < min)
            {
                int newCapacity = _buffer.Length == 0 ? Math.Max(4, _minGrow) : _buffer.Length * 2;

                // 确保不超过最大数组长度
                if (newCapacity > MaxArrayLength)
                {
                    newCapacity = MaxArrayLength;
                }

                if (newCapacity < min)
                {
                    newCapacity = min;
                }

                Array.Resize(ref _buffer, newCapacity);
            }
        }

        /// <summary>
        /// 索引器访问
        /// </summary>
        public T this[int index]
        {
            get
            {
                if (index < 0 || index >= _count)
                    throw new IndexOutOfRangeException();

                if (_freeSlots.Contains(index))
                    throw new InvalidOperationException("Accessing deleted element");

                return _buffer[index];
            }
            set
            {
                if (index < 0 || index >= _count)
                    throw new IndexOutOfRangeException();

                if (!_freeSlots.Contains(index))
                {
                    _buffer[index] = value;
                    _version++;
                }
            }
        }

        #endregion
    }

    /// <summary>
    /// 清空模式选项
    /// </summary>
    public enum ClearMode
    {
        /// <summary> 根据数据量自动选择最佳策略 </summary>
        Auto = 0,

        /// <summary> 立即清空（适合小数据集） </summary>
        Immediate = 1,

        /// <summary> 标记删除（适合大数据集） </summary>
        MarkDeleted = 2,

        /// <summary> 彻底释放内存 </summary>
        ReleaseMemory = 3
    }
}