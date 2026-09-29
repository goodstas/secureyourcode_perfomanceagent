namespace SecureYourCode.PerformanceAnalyzer.Tests;

public class QueryInLoopAnalyzerTests
{
    private const string Repository = """
        using System.Collections.Generic;
        using System.Linq;
        using System.Threading.Tasks;

        public sealed record Customer(int Id, string Name);
        public sealed record Order(int Id, int CustomerId, int[] ProductIds);

        public interface ICustomerRepository
        {
            Task<Customer> GetByIdAsync(int id);
            Task<IReadOnlyDictionary<int, Customer>> GetByIdsAsync(IReadOnlyCollection<int> ids);
            Task<IReadOnlyList<Customer>> GetAllAsync();
        }
        """;

    /// <summary>Minimal stand-ins for the EF Core types the rule recognises (matched by metadata name).</summary>
    private const string EfCoreStubs = """
        using System;
        using System.Collections;
        using System.Collections.Generic;
        using System.Linq;
        using System.Linq.Expressions;
        using System.Threading;
        using System.Threading.Tasks;

        namespace Microsoft.EntityFrameworkCore
        {
            public class DbContext
            {
                public int SaveChanges() => 0;
                public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
            }

            public abstract class DbSet<T> : IQueryable<T> where T : class
            {
                public abstract Type ElementType { get; }
                public abstract Expression Expression { get; }
                public abstract IQueryProvider Provider { get; }
                public abstract IEnumerator<T> GetEnumerator();
                IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
                public ValueTask<T?> FindAsync(params object?[]? keyValues) => default;
            }

            public static class EntityFrameworkQueryableExtensions
            {
                public static Task<List<T>> ToListAsync<T>(this IQueryable<T> source, CancellationToken cancellationToken = default) =>
                    Task.FromResult(new List<T>());
                public static Task<T?> FirstOrDefaultAsync<T>(this IQueryable<T> source, CancellationToken cancellationToken = default) =>
                    Task.FromResult<T?>(default);
                public static Task<int> CountAsync<T>(this IQueryable<T> source, CancellationToken cancellationToken = default) =>
                    Task.FromResult(0);
            }
        }

        public sealed class User { public int Id { get; set; } }

        public sealed class ShopContext : Microsoft.EntityFrameworkCore.DbContext
        {
            public Microsoft.EntityFrameworkCore.DbSet<User> Users { get; } = null!;
        }
        """;

    [Fact]
    public Task RepositoryCallInForeach_Reports() => Verify<QueryInLoopAnalyzer>.AnalyzerAsync(Repository + """

        public sealed class OrderSummaryService(ICustomerRepository customers)
        {
            public async Task<List<string>> BuildSummariesAsync(IReadOnlyList<Order> orders)
            {
                var names = new List<string>();
                foreach (var order in orders)
                {
                    var customer = await {|PERF001:customers.GetByIdAsync(order.CustomerId)|};
                    names.Add(customer.Name);
                }

                return names;
            }
        }
        """);

    [Fact]
    public Task RepositoryCallInNestedLoops_ReportsTheInnerCallOnce() => Verify<QueryInLoopAnalyzer>.AnalyzerAsync(Repository + """

        public sealed class InvoiceService(ICustomerRepository customers)
        {
            public async Task<int> CountAsync(IReadOnlyList<Order> orders)
            {
                var count = 0;
                foreach (var order in orders)
                {
                    foreach (var productId in order.ProductIds)
                    {
                        var customer = await {|PERF001:customers.GetByIdAsync(productId)|};
                        count += customer.Id;
                    }
                }

                return count;
            }
        }
        """);

    [Fact]
    public Task RepositoryCallInForAndWhileLoops_Reports() => Verify<QueryInLoopAnalyzer>.AnalyzerAsync(Repository + """

        public sealed class Loops(ICustomerRepository customers)
        {
            public async Task RunAsync(int[] ids)
            {
                for (var i = 0; i < ids.Length; i++)
                {
                    await {|PERF001:customers.GetByIdAsync(ids[i])|};
                }

                var j = 0;
                while (j < ids.Length)
                {
                    await {|PERF001:customers.GetByIdAsync(ids[j++])|};
                }
            }
        }
        """);

    [Fact]
    public Task EfCoreQueryExecutionInLoop_Reports() => Verify<QueryInLoopAnalyzer>.AnalyzerAsync(EfCoreStubs + """

        public sealed class UserService(ShopContext db)
        {
            public async Task RunAsync(int[] ids)
            {
                foreach (var id in ids)
                {
                    _ = {|PERF001:db.Users.Where(u => u.Id == id).FirstOrDefault()|};
                    _ = await {|PERF001:Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(db.Users.Where(u => u.Id > id))|};
                    _ = await {|PERF001:db.Users.FindAsync(id)|};
                    await {|PERF001:db.SaveChangesAsync()|};
                }
            }
        }
        """);

    [Fact]
    public Task BatchedLookupBeforeTheLoop_NoDiagnostic() => Verify<QueryInLoopAnalyzer>.AnalyzerAsync(Repository + """

        public sealed class BatchedOrderService(ICustomerRepository customers)
        {
            public async Task<List<string>> BuildSummariesAsync(IReadOnlyList<Order> orders)
            {
                var byId = await customers.GetByIdsAsync(orders.Select(o => o.CustomerId).Distinct().ToArray());
                var names = new List<string>();
                foreach (var order in orders)
                {
                    names.Add(byId[order.CustomerId].Name);
                }

                return names;
            }
        }
        """);

    [Fact]
    public Task BuildingAQueryableInLoopWithoutExecuting_NoDiagnostic() => Verify<QueryInLoopAnalyzer>.AnalyzerAsync(EfCoreStubs + """

        public sealed class UserQuery(ShopContext db)
        {
            public IQueryable<User> Build(int[] excluded)
            {
                IQueryable<User> query = db.Users;
                foreach (var id in excluded)
                {
                    query = query.Where(u => u.Id != id);
                }

                return query;
            }
        }
        """);

    [Fact]
    public Task LinqToObjectsInLoop_NoDiagnostic() => Verify<QueryInLoopAnalyzer>.AnalyzerAsync("""
        using System.Collections.Generic;
        using System.Linq;

        public static class InMemory
        {
            public static int Run(List<int> values)
            {
                var total = 0;
                foreach (var v in values)
                {
                    total += values.Where(x => x > v).ToList().Count + values.Count();
                }

                return total;
            }
        }
        """);

    [Fact]
    public Task CallInForeachCollectionExpression_NoDiagnostic() => Verify<QueryInLoopAnalyzer>.AnalyzerAsync(Repository + """

        public sealed class Listing(ICustomerRepository customers)
        {
            public async Task<int> RunAsync()
            {
                var total = 0;
                foreach (var customer in await customers.GetAllAsync())
                {
                    total += customer.Id;
                }

                return total;
            }
        }
        """);
}
