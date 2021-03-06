﻿using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using Enmap.Utils;

namespace Enmap
{
    public class ContainerFetcher
    {
        private Type sourceType;
        private Type destinationType;
        private MethodInfo where;
        private MethodInfo contains;
        private MethodInfo cast;
        private MethodInfo select;
        private MethodInfo selectMany;
        private MethodInfo toArrayAsync;
        private Type primaryEntityType;
        private LambdaExpression primaryEntityRelationship;
        private PropertyInfo primaryEntityKeyProperty;
        private PropertyInfo dependentEntityKeyProperty;
        private Type fetchKeyType;
        private PropertyInfo parentIdProperty;
        private PropertyInfo childIdProperty;

        public ContainerFetcher(IMapperRegistry registry, Type sourceType, Type destinationType, Type primaryEntityType, LambdaExpression primaryEntityRelationship)
        {
            this.sourceType = sourceType;
            this.destinationType = destinationType;
            this.primaryEntityType = primaryEntityType;
            this.primaryEntityRelationship = primaryEntityRelationship;

            var primaryEntitySet = registry.Metadata.EntitySets.Single(x => x.ElementType.FullName == primaryEntityType.FullName);
            var primaryEntityKeyEfProperty = primaryEntitySet.ElementType.KeyProperties.Single();
            primaryEntityKeyProperty = primaryEntityType.GetProperty(primaryEntityKeyEfProperty.Name);

            var dependentEntitySet = registry.Metadata.EntitySets.Single(x => x.ElementType.FullName == sourceType.FullName);
            var dependentEntityKeyEfProperty = dependentEntitySet.ElementType.KeyProperties.Single();
            dependentEntityKeyProperty = sourceType.GetProperty(dependentEntityKeyEfProperty.Name);

            fetchKeyType = typeof(FetchKeyPair<,>).MakeGenericType(primaryEntityKeyProperty.PropertyType, dependentEntityKeyProperty.PropertyType);
            parentIdProperty = fetchKeyType.GetProperty("ParentId");
            childIdProperty = fetchKeyType.GetProperty("ChildId");
            where = typeof(Queryable).GetMethods().Single(x => x.Name == "Where" && x.GetParameters()[1].ParameterType.GetGenericArguments()[0].GetGenericArguments().Length == 2).MakeGenericMethod(primaryEntityType);
            cast = typeof(Enumerable).GetMethods().Single(x => x.Name == "Cast").MakeGenericMethod(primaryEntityKeyProperty.PropertyType);
            select = typeof(Queryable).GetMethods().Single(x => x.Name == "Select" && x.GetParameters()[1].ParameterType.GetGenericArguments()[0].GetGenericArguments().Length == 2).MakeGenericMethod(primaryEntityType, fetchKeyType);
            selectMany = typeof(Queryable).GetMethods().Single(x => x.Name == "SelectMany" && x.GetGenericArguments().Length == 2 && x.GetParameters()[1].ParameterType.GetGenericArguments()[0].GetGenericArguments().Length == 2).MakeGenericMethod(primaryEntityType, fetchKeyType);
            contains = typeof(Enumerable).GetMethods().Single(x => x.Name == "Contains" && x.GetParameters().Length == 2).MakeGenericMethod(primaryEntityKeyProperty.PropertyType);
            toArrayAsync = typeof(QueryableExtensions).GetMethods().Single(x => x.Name == "ToArrayAsync" && x.GetParameters().Length == 1).MakeGenericMethod(fetchKeyType);
        }

        public async Task Apply(IEnumerable<ReverseEntityFetcherItem> items, MapperContext context)
        {
            // Assemble ids
            var ids = cast.Invoke(null, new object[] { items.Select(x => x.EntityId).Distinct().ToArray() });
            var itemsById = items.ToLookup(x => x.EntityId);

            // Our queryable object from which we can grab the dependent items
            var dbSet = context.DbContext.Set(primaryEntityType);

            // Build where predicate
            var entityParameter = Expression.Parameter(primaryEntityType);
            var wherePredicate = Expression.Lambda(
                Expression.Call(contains, Expression.Constant(ids), Expression.MakeMemberAccess(entityParameter, primaryEntityKeyProperty)),
                entityParameter);
            var queryable = (IQueryable)where.Invoke(null, new object[] { dbSet, wherePredicate });

            var mainBinder = new LambdaBinder();
            var obj = Expression.Parameter(primaryEntityType);
            var body = mainBinder.BindBody(primaryEntityRelationship, obj, Expression.Constant(context, context.GetType()));
            if (primaryEntityRelationship.Body.Type.IsGenericEnumerable())
            {
                var selectMethod = typeof(Enumerable).GetMethods().Single(x => x.Name == "Select" && x.GetParameters().Length == 2 && x.GetParameters()[1].ParameterType.GetGenericTypeDefinition() == typeof(Func<,>)).MakeGenericMethod(sourceType, fetchKeyType);
                var subParameter = Expression.Parameter(sourceType);
                var subLambda = Expression.Lambda(typeof(Func<,>).MakeGenericType(sourceType, fetchKeyType),
                    Expression.MemberInit(
                        Expression.New(fetchKeyType), 
                        Expression.Bind(parentIdProperty, Expression.MakeMemberAccess(obj, primaryEntityKeyProperty)),
                        Expression.Bind(childIdProperty, Expression.MakeMemberAccess(subParameter, dependentEntityKeyProperty))),
                    subParameter);
                body = Expression.Call(selectMethod, body, subLambda);
                var lambda = Expression.Lambda(
                    typeof(Func<,>).MakeGenericType(primaryEntityType, typeof(IEnumerable<>).MakeGenericType(fetchKeyType)),
                    body, 
                    obj);
                queryable = (IQueryable)selectMany.Invoke(null, new object[] { queryable, lambda });                    
            }
            else
            {
                body = Expression.MemberInit(
                    Expression.New(fetchKeyType), 
                    Expression.Bind(parentIdProperty, Expression.MakeMemberAccess(obj, primaryEntityKeyProperty)),
                    Expression.Bind(childIdProperty, Expression.MakeMemberAccess(body, dependentEntityKeyProperty)));
                var lambda = Expression.Lambda(
                    typeof(Func<,>).MakeGenericType(primaryEntityType, fetchKeyType),
                    body, 
                    obj);
                queryable = (IQueryable)select.Invoke(null, new object[] { queryable, lambda });
            }

            var task = (Task)toArrayAsync.Invoke(null, new object[] { queryable });
            await task;

            var destinationPairs = ((Array)task.GetType().GetProperty("Result").GetValue(task, null)).Cast<IFetchKeyPair>().ToArray();
            var destinationIds = destinationPairs.Select(x => x.ChildId).ToArray();
            var parentIdsByDestinationId = destinationPairs.ToLookup(x => x.ChildId, x => x.ParentId);
            var results = await context.Registry.GlobalCache.GetByIds(sourceType, destinationType, destinationIds, context);
                
            var destinationsByItem = new Dictionary<IFetcherItem, List<object>>();
            var primaryKeyProperty = destinationType.GetProperty("Id");
            if (primaryKeyProperty == null)
                throw new Exception("No id found on destination type: " + destinationType.FullName);
            foreach (var result in results)
            {
                var destinationId = primaryKeyProperty.GetValue(result, null);
                var parentIds = parentIdsByDestinationId[destinationId];
                foreach (var parentId in parentIds)
                {
                    var itemSet = itemsById[parentId];
                    foreach (var item in itemSet)
                    {
                        List<object> destinations;
                        if (!destinationsByItem.TryGetValue(item, out destinations))
                        {
                            destinations = new List<object>();
                            destinationsByItem[item] = destinations;
                        }
                        destinations.Add(result);                        
                    }                        
                }
            }

            foreach (var item in items)
            {
                if (!destinationsByItem.ContainsKey(item))
                {
                    destinationsByItem[item] = new List<object>();
                }
            }
                    
            foreach (var current in destinationsByItem)
            {
                var item = current.Key;
                var destinations = current.Value;
                await item.ApplyFetchedValue(destinations.ToArray());
            }
        }
    }
}