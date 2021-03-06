﻿using System;

namespace Neutronium.Core.Infra
{
    internal class Chained<T> where T : class
    {
        internal T Value { get; }

        internal Chained<T> Next { get; set; }

        internal Chained(T current, Chained<T> previous)
        {
            Value = current;
            if (previous != null)
                previous.Next = this;
        }

        internal void ForEach(Action<T> perform)
        {
            var current = this;
            while (current != null)
            {
                perform(current.Value);
                current = current.Next;
            }
        }

        internal T Reduce(Func<T, T, T> agregate, T from = default(T)) => Reduce<T, T>(Identity, agregate, from);

        private static T Identity(T value) => value;

        internal TValue Reduce<TValue>(Func<T, TValue> compute, Func<TValue, TValue, TValue> agregate, TValue from = default(TValue))
            => Reduce<TValue, TValue>(compute, agregate, from);

        internal TResult Reduce<TValue, TResult>(Func<T, TValue> compute, Func<TResult, TValue, TResult> agregate, TResult from = default(TResult))
        {
            var current = this;
            var result = from;
            while (current != null)
            {
                var value = compute(current.Value);
                result = agregate(result, value);
                current = current.Next;
            }
            return result;
        }
    }
}
