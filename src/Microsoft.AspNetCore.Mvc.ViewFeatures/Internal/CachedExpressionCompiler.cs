// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace Microsoft.AspNetCore.Mvc.ViewFeatures
{
    internal static class CachedExpressionCompiler
    {
        private static readonly Expression NullExpression = Expression.Constant(value: null);
        private static readonly MethodInfo ObjectReferenceEquals = typeof(object).GetMethod(
            nameof(object.ReferenceEquals),
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            new[] { typeof(object), typeof(object) },
            modifiers: null);

        // This is the entry point to the cached expression compilation system. The system
        // will try to turn the expression into an actual delegate as quickly as possible,
        // relying on cache lookups and other techniques to save time if appropriate.
        // If the provided expression is particularly obscure and the system doesn't know
        // how to handle it, we'll just compile the expression as normal.
        public static Func<TModel, object> Process<TModel, TResult>(
            Expression<Func<TModel, TResult>> expression)
        {
            if (expression == null)
            {
                throw new ArgumentNullException(nameof(expression));
            }

            return Compiler<TModel, TResult>.Compile(expression);
        }

        private static class Compiler<TModel, TResult>
        {
            private static Func<TModel, object> _identityFunc;

            private static readonly ConcurrentDictionary<MemberInfo, Func<TModel, object>> _simpleMemberAccessCache =
                new ConcurrentDictionary<MemberInfo, Func<TModel, object>>();

            private static readonly ConcurrentDictionary<MemberInfo, Func<TModel, object>> _constMemberAccessCache =
                new ConcurrentDictionary<MemberInfo, Func<TModel, object>>();

            public static Func<TModel, object> Compile(Expression<Func<TModel, TResult>> expression)
            {
                switch (expression.Body)
                {
                    // model => model
                    case var body when body == expression.Parameters[0]:
                        return CompileFromIdentityFunc(expression);

                    // model => (object){const}
                    case ConstantExpression constantExpression:
                        return CompileFromConstLookup(constantExpression);

                    // model => StaticMember
                    case MemberExpression memberExpression when memberExpression.Expression == null:
                        return CompileFromStaticMemberAccess(expression, memberExpression);

                    // model => CapturedConstant
                    case MemberExpression memberExpression when memberExpression.Expression is ConstantExpression constantExpression:
                        return CompileCapturedConstant(expression, memberExpression, constantExpression);

                    // model => model.Property
                    case MemberExpression memberExpression when memberExpression.Expression == expression.Parameters[0]:
                        return CompileFromSimpleMemberAccess(expression, memberExpression);

                    case MemberExpression memberExpression when IsChainedPropertyAccessor(memberExpression):
                        return CompileForChainedMemberAccess(expression);

                    default:
                        return CompileSlow(expression);
                }

                bool IsChainedPropertyAccessor(MemberExpression memberExpression)
                {
                    while (memberExpression.Expression != null)
                    {
                        if (memberExpression.Expression is MemberExpression parentExpression)
                        {
                            memberExpression = parentExpression;
                            continue;
                        }
                        else if (memberExpression.Expression == expression.Parameters[0])
                        {
                            return true;
                        }

                        break;
                    }

                    return false;
                }
            }

            private static Func<TModel, object> CompileFromConstLookup(
                ConstantExpression constantExpression)
            {
                var constantValue = constantExpression.Value;
                return _ => constantValue;
            }

            private static Func<TModel, object> CompileFromIdentityFunc(
                Expression<Func<TModel, TResult>> expression)
            {
                // Don't need to lock, as all identity funcs are identical.
                if (_identityFunc == null)
                {
                    var identityFuncCore = expression.Compile();
                    _identityFunc = model => identityFuncCore(model);
                }

                return _identityFunc;
            }

            private static Func<TModel, object> CompileFromStaticMemberAccess(
                Expression<Func<TModel, TResult>> expression,
                MemberExpression memberExpression)
            {
                if (_simpleMemberAccessCache.TryGetValue(memberExpression.Member, out var result))
                {
                    return result;
                }

                var func = expression.Compile();
                result = model => func(model);
                _simpleMemberAccessCache.TryAdd(memberExpression.Member, result);

                return result;
            }

            private static Func<TModel, object> CompileFromSimpleMemberAccess(
                Expression<Func<TModel, TResult>> expression,
                MemberExpression memberExpression)
            {
                // Input: () => m.Member1.Member
                // Output: () => (m == null) ? null : m.Member1

                if (_simpleMemberAccessCache.TryGetValue(memberExpression.Member, out var result))
                {
                    return result;
                }

                var rewrittenExpression = Rewrite(expression, (MemberExpression)expression.Body);
                result = ((Expression<Func<TModel, object>>)rewrittenExpression).Compile();

                _simpleMemberAccessCache.TryAdd(memberExpression.Member, result);

                return result;
            }

            private static Func<TModel, object> CompileForChainedMemberAccess(Expression<Func<TModel, TResult>> expression)
            {
                // Input: () => m.Member1.Member
                // Output: () => (m == null || m.Member1 == null) ? null : m.Member1.Member2
                var rewrittenExpression = Rewrite(expression, (MemberExpression)expression.Body);
                return ((Expression<Func<TModel, object>>)rewrittenExpression).Compile();
            }

            private static Func<TModel, object> CompileCapturedConstant(
                Expression<Func<TModel, TResult>> expression,
                MemberExpression memberExpression,
                ConstantExpression constantExpression)
            {
                if (_constMemberAccessCache.TryGetValue(memberExpression.Member, out var result))
                {
                    return result;
                }

                // model => {const} (captured local variable)
                // rewrite as capturedLocal => ((TDeclaringType)capturedLocal)
                var parameterExpression = Expression.Parameter(typeof(object), "capturedLocal");
                var castExpression =
                    Expression.Convert(parameterExpression, memberExpression.Member.DeclaringType);
                var replacementMemberExpression = memberExpression.Update(castExpression);
                var replacementExpression = Expression.Lambda<Func<object, object>>(
                    Expression.Convert(replacementMemberExpression, typeof(object)),
                    parameterExpression);

                var compiledExpression = replacementExpression.Compile();

                var capturedLocal = constantExpression.Value;
                result = _ => compiledExpression(capturedLocal);
                _constMemberAccessCache.TryAdd(memberExpression.Member, result);
                return result;
            }

            private static Func<TModel, object> CompileSlow(Expression<Func<TModel, TResult>> expression)
            {
                // fallback compilation system - just compile the expression directly
                var compiledExpression = expression.Compile();
                return model => compiledExpression(model);
            }

            private static Expression Rewrite(Expression<Func<TModel, TResult>> expression, MemberExpression memberExpression)
            {
                Expression combinedNullTest = null;
                var currentExpression = memberExpression;

                while (currentExpression != null)
                {
                    AddNullCheck(currentExpression.Expression);

                    if (currentExpression.Expression is MemberExpression parentMemberExpression)
                    {
                        currentExpression = parentMemberExpression;
                    }
                    else
                    {
                        break;
                    }
                }

                var body = expression.Body;

                // Cast the entire expression to object in case Member is a value type. This allows us to construct a Func<TModel, object>
                if (body.Type.IsValueType)
                {
                    body = Expression.Convert(body, typeof(object));
                }

                if (combinedNullTest != null)
                {
                    body = Expression.Condition(
                        combinedNullTest,
                        Expression.Constant(value: null, body.Type),
                        body);
                }

                return Expression.Lambda<Func<TModel, object>>(body, expression.Parameters);

                void AddNullCheck(Expression invokingExpression)
                {
                    var type = invokingExpression.Type;
                    var isNullableType = Nullable.GetUnderlyingType(type) != null;
                    if (type.IsValueType && !isNullableType)
                    {
                        // struct.Member where struct is not nullable. Do nothing.
                        return;
                    }

                    // NullableStruct.Member or Class.Member
                    // type is Nullable ? (value == null) : object.ReferenceEquals(value, null)
                    var nullTest = isNullableType ?
                        Expression.Equal(invokingExpression, NullExpression) :
                        (Expression)Expression.Call(ObjectReferenceEquals, invokingExpression, NullExpression);

                    if (combinedNullTest == null)
                    {
                        combinedNullTest = nullTest;
                    }
                    else
                    {
                        // m == null || m.Member == null
                        combinedNullTest = Expression.OrElse(nullTest, combinedNullTest);
                    }
                }
            }
        }
    }
}