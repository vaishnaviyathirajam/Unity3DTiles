﻿/*
 * Copyright 2018, by the California Institute of Technology. ALL RIGHTS 
 * RESERVED. United States Government Sponsorship acknowledged. Any 
 * commercial use must be negotiated with the Office of Technology 
 * Transfer at the California Institute of Technology.
 * 
 * This software may be subject to U.S.export control laws.By accepting 
 * this software, the user agrees to comply with all applicable 
 * U.S.export laws and regulations. User has the responsibility to 
 * obtain export licenses, or other export authority as may be required 
 * before exporting such information to foreign countries or providing 
 * access to foreign persons.
 */
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Unity3DTiles
{
    /// <summary>
    /// Represents a least recently used (LRU) cache
    /// </summary>
    public class LRUCache<T> where T : class
    {

        // List looks like this
        // [ unused, sential, used ]

        LinkedList<T> list = new LinkedList<T>();
        LinkedListNode<T> sentinal = new LinkedListNode<T>(null);

        Dictionary<T, LinkedListNode<T>> nodeLookup = new Dictionary<T, LinkedListNode<T>>();

        /// <summary>
        /// Number of items in cache O(1)
        /// </summary>
        public int Count
        {
            get { return list.Count - 1; }
        }

        /// <summary>
        /// Number of unused items in the cache O(N) where N is number of unused items
        /// </summary>
        public int Unused
        {
            get
            {
                int count = 0;
                LinkedListNode<T> node = this.sentinal;
                while (node.Previous != null)
                {
                    count++;
                    node = node.Previous;
                }
                return count;
            }
        }

        public int Used
        {
            get
            {
                int count = 0;
                LinkedListNode<T> node = this.sentinal;
                while (node.Next != null)
                {
                    count++;
                    node = node.Next;
                }
                return count;
            }
        }

        public LRUCache()
        {
            list.AddFirst(sentinal);
        }

        /// <summary>
        /// Adds a new element to the replacement list and marks it as used.  Returns the node for that element
        /// </summary>
        /// <param name="element"></param>
        /// <returns></returns>
        public void Add(T element)
        {
            if (nodeLookup.ContainsKey(element))
            {
                return;
            }
            LinkedListNode<T> node = new LinkedListNode<T>(element);
            nodeLookup.Add(element, node);
            list.AddLast(node);
        }

        /// <summary>
        /// Marks this node as used
        /// </summary>
        /// <param name="node"></param>
        public void MarkUsed(T element)
        {
            if (nodeLookup.ContainsKey(element))
            {
                var node = nodeLookup[element];
                this.list.Remove(node);
                this.list.AddLast(node);
            }
        }

        /// <summary>
        /// Marks all nodes as unused
        /// </summary>
        public void MarkAllUnused()
        {
            this.list.Remove(sentinal);
            this.list.AddLast(sentinal);
        }

        /// <summary>
        /// Removes the least recently used element and returns it
        /// </summary>
        /// <returns></returns>
        public T RemoveLeastRecentlyUsed()
        {
            if (this.list.First == this.sentinal)
            {
                return null;
            }
            T element = this.list.First.Value;
            this.list.RemoveFirst();
            nodeLookup.Remove(element);
            return element;
        }

        /// <summary>
        /// Removes all unused elements and returns them in order of least recently used to most recently used
        /// </summary>
        /// <returns></returns>
        public List<T> RemoveUnused()
        {
            List<T> list = new List<T>();
            while (this.list.First != this.sentinal)
            {
                T element = this.list.First.Value;
                nodeLookup.Remove(element);
                this.list.RemoveFirst();
                list.Add(element);
            }
            return list;
        }

        /// <summary>
        /// Returns a list of unused nodes but does not remove them
        /// </summary>
        /// <returns></returns>
        public List<T> GetUnused()
        {
            List<T> result = new List<T>();
            LinkedListNode<T> curNode = this.list.First;
            while (curNode != this.sentinal)
            {
                result.Add(curNode.Value);
                curNode = curNode.Next;
            }
            return result;
        }

        /// <summary>
        /// Remove a specific node regardless of its state (used or unused) 
        /// </summary>
        /// <param name="node"></param>
        public void Remove(T element)
        {
            if (nodeLookup.ContainsKey(element))
            {
                var node = nodeLookup[element];
                nodeLookup.Remove(element);
                this.list.Remove(node);
            }

        }
    }
}
