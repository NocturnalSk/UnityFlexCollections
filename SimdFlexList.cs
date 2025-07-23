using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace UnityFlexCollections
{
    [BurstCompile]
    public unsafe struct AtomicInt
    {
        private int* _value;

        public AtomicInt(int* ptr)
        {
            _value = ptr;
        }

        [BurstCompile(CompileSynchronously = true)]
        public int Increment()
        {
            return Interlocked.Increment(ref UnsafeUtility.AsRef<int>(_value));
        }

        [BurstCompile(CompileSynchronously = true)]
        public int Decrement()
        {
            return Interlocked.Decrement(ref UnsafeUtility.AsRef<int>(_value));
        }

        [BurstCompile(CompileSynchronously = true)]
        public int CompareExchange(int value, int comparand)
        {
            return Interlocked.CompareExchange(ref UnsafeUtility.AsRef<int>(_value), value, comparand);
        }

        [BurstCompile(CompileSynchronously = true)]
        public int Exchange(int value)
        {
            return Interlocked.Exchange(ref UnsafeUtility.AsRef<int>(_value), value);
        }

        public int Value => UnsafeUtility.ReadArrayElement<int>(_value, 0);
    }

    [BurstCompatible]
    public unsafe struct ThreadSafeSimdFlexList<T> where T : unmanaged
    {
        private const int Capacity = 256;
        private const int FreeMapSize = 32; // 256 bits (256/8=32)
        
        // 使用 NativeArray 确保线程安全的内存分配
        [NativeDisableUnsafePtrRestriction] private byte* _buffer;
        [NativeDisableUnsafePtrRestriction] private byte* _freeMap;
        [NativeDisableUnsafePtrRestriction] private int* _count;
        [NativeDisableUnsafePtrRestriction] private int* _freeCount;
        
        private Allocator _allocator;

        public bool IsCreated => _buffer != null;

        public int Count
        {
            get
            {
                if (!IsCreated) return 0;
                return UnsafeUtility.ReadArrayElement<int>(_count, 0) - 
                       UnsafeUtility.ReadArrayElement<int>(_freeCount, 0);
            }
        }

        public ThreadSafeSimdFlexList(Allocator allocator)
        {
            _allocator = allocator;
            
            // 分配内存
            int totalSize = 
                Capacity * UnsafeUtility.SizeOf<T>() + // Buffer
                FreeMapSize +                         // Free map
                sizeof(int) * 3;                       // Count + free count
            
            _buffer = (byte*)UnsafeUtility.Malloc(
                totalSize, 
                UnsafeUtility.AlignOf<T>(), 
                allocator
            );
            
            // 设置指针位置
            _freeMap = _buffer + Capacity * UnsafeUtility.SizeOf<T>();
            _count = (int*)(_freeMap + FreeMapSize);
            _freeCount = _count + 1;
            
            // 初始化
            UnsafeUtility.MemClear(_buffer, totalSize);
            UnsafeUtility.MemSet(_freeMap, 0xFF, FreeMapSize); // 所有位设置为1（空闲）
        }

        public void Dispose()
        {
            if (IsCreated)
            {
                UnsafeUtility.Free(_buffer, _allocator);
                _buffer = null;
                _freeMap = null;
                _count = null;
                _freeCount = null;
            }
        }

        private ref T GetElement(int index)
        {
            return ref UnsafeUtility.AsRef<T>(_buffer + index * UnsafeUtility.SizeOf<T>());
        }

        public bool TryGet(int index, out T value)
        {
            if (!IsCreated || !IsValidIndex(index))
            {
                value = default;
                return false;
            }
            
            value = GetElement(index);
            return true;
        }

        public bool Set(int index, in T value)
        {
            if (!IsCreated || !IsValidIndex(index))
                return false;
            
            GetElement(index) = value;
            return true;
        }

        private bool IsSlotFree(int index)
        {
            int byteIdx = index / 8;
            int bitIdx = index % 8;
            return (_freeMap[byteIdx] & (1 << bitIdx)) != 0;
        }

        private bool SetSlotState(int index, bool isFree)
        {
            int byteIdx = index / 8;
            int bitIdx = index % 8;
            
            AtomicInt freeCount = new AtomicInt(_freeCount);
            
            if (isFree)
            {
                // 设置位
                byte oldValue;
                do
                {
                    oldValue = _freeMap[byteIdx];
                    byte newValue = (byte)(oldValue | (1 << bitIdx));
                    
                    if (Interlocked.CompareExchange(
                        ref UnsafeUtility.AsRef<byte>(_freeMap + byteIdx), 
                        newValue, 
                        oldValue) == oldValue)
                    {
                        freeCount.Increment();
                        return true;
                    }
                } while (true);
            }
            else
            {
                // 清除位
                byte oldValue;
                do
                {
                    oldValue = _freeMap[byteIdx];
                    byte newValue = (byte)(oldValue & ~(1 << bitIdx));
                    
                    if (Interlocked.CompareExchange(
                        ref UnsafeUtility.AsRef<byte>(_freeMap + byteIdx), 
                        newValue, 
                        oldValue) == oldValue)
                    {
                        freeCount.Decrement();
                        return true;
                    }
                } while (true);
            }
        }

        public bool IsValidIndex(int index)
        {
            if (!IsCreated || index < 0 || index >= Capacity)
                return false;
            
            return !IsSlotFree(index);
        }

        public int Add(in T item)
        {
            if (!IsCreated)
                return -1;
            
            AtomicInt count = new AtomicInt(_count);
            AtomicInt freeCount = new AtomicInt(_freeCount);
            
            // 快速路径：有空闲槽位
            if (freeCount.Value > 0)
            {
                // 查找第一个空闲槽位 (原子操作)
                for (int i = 0; i < Capacity; i++)
                {
                    if (IsSlotFree(i))
                    {
                        if (SetSlotState(i, false))
                        {
                            GetElement(i) = item;
                            return i;
                        }
                    }
                }
            }
            
            // 慢速路径：添加新元素
            int newIndex;
            do
            {
                int currentCount = count.Value;
                if (currentCount >= Capacity)
                    return -1; // 列表已满
                
                newIndex = currentCount;
                
                // 原子增加计数
                if (count.CompareExchange(currentCount + 1, currentCount) == currentCount)
                {
                    GetElement(newIndex) = item;
                    return newIndex;
                }
            } while (true);
        }

        public bool RemoveAt(int index)
        {
            if (!IsCreated || !IsValidIndex(index))
                return false;
            
            return SetSlotState(index, true);
        }

        public void Clear()
        {
            if (!IsCreated) return;
            
            // 重置计数
            AtomicInt count = new AtomicInt(_count);
            AtomicInt freeCount = new AtomicInt(_freeCount);
            
            count.Exchange(0);
            freeCount.Exchange(0);
            
            // 设置所有位为空闲
            for (int i = 0; i < FreeMapSize; i++)
            {
                Interlocked.Exchange(ref UnsafeUtility.AsRef<byte>(_freeMap + i), 0xFF);
            }
        }

        public void ForEach(Action<int, T> action)
        {
            if (!IsCreated) return;
            
            for (int i = 0; i < Capacity; i++)
            {
                if (IsValidIndex(i))
                {
                    action(i, GetElement(i));
                }
            }
        }

        public int FirstIndexWhere(Func<T, bool> predicate)
        {
            if (!IsCreated) return -1;
            
            for (int i = 0; i < Capacity; i++)
            {
                if (IsValidIndex(i) && predicate(GetElement(i)))
                    return i;
            }
            return -1;
        }

        public int FindAll(NativeArray<T> resultBuffer)
        {
            if (!IsCreated) return 0;
            
            int j = 0;
            for (int i = 0; i < Capacity && j < resultBuffer.Length; i++)
            {
                if (IsValidIndex(i))
                {
                    resultBuffer[j++] = GetElement(i);
                }
            }
            return j;
        }

        public NativeArray<T> ToNativeArray(Allocator allocator)
        {
            if (!IsCreated) return new NativeArray<T>(0, allocator);
            
            int validCount = Count;
            NativeArray<T> arr = new NativeArray<T>(validCount, allocator);
            
            int j = 0;
            for (int i = 0; i < Capacity && j < validCount; i++)
            {
                if (IsValidIndex(i))
                {
                    arr[j++] = GetElement(i);
                }
            }
            return arr;
        }

        public void Compact()
        {
            if (!IsCreated) return;
            
            // 在 JobSystem 中，压缩需要同步操作，这里不实现
            // 建议在单线程环境中执行压缩
        }

        public void Sort(Comparison<T> comparison)
        {
            if (!IsCreated || Count < 2) return;
            
            // 排序需要在单线程环境中执行
            using (var array = ToNativeArray(Allocator.Temp))
            {
                // 使用 NativeArray 的排序
                array.Sort(comparison);
                
                // 重新填充列表
                Clear();
                for (int i = 0; i < array.Length; i++)
                {
                    Add(array[i]);
                }
            }
        }

        public int CapacityMax => Capacity;
    }

    // 示例 Job 使用线程安全列表
    [BurstCompile]
    public struct AddToFlexListJob<T> : IJobParallelFor where T : unmanaged
    {
        public ThreadSafeSimdFlexList<T> List;
        [ReadOnly] public NativeArray<T> Items;

        public void Execute(int index)
        {
            List.Add(Items[index]);
        }
    }

    [BurstCompile]
    public struct ProcessFlexListJob<T> : IJob where T : unmanaged
    {
        public ThreadSafeSimdFlexList<T> List;
        public Func<T, T> Processor;

        public void Execute()
        {
            for (int i = 0; i < List.CapacityMax; i++)
            {
                if (List.TryGet(i, out T value))
                {
                    List.Set(i, Processor(value));
                }
            }
        }
    }
}