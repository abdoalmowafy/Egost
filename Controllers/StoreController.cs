﻿using Egost.Areas.Identity.Data;
using Egost.Data;
using Egost.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Collections.Generic;

namespace Egost.Controllers
{
    public class StoreController(EgostContext db) : Controller
    {
        private readonly EgostContext _db = db;
        private IEnumerable<string> GetCategoriesNames() => _db.Categories.Select(c => c.Name);


        public IActionResult Home()
        {
            IEnumerable<OrderProduct> OrderedProducts = _db.OrderProducts.Include(op => op.Product);
            IEnumerable<Product> NonDeletedAvailableProducts = _db.Products
                .Include(p => p.Category).Where(p => p.DeletedDateTime == null && p.SKU > 0);

            IDictionary<Product, int> freq = new Dictionary<Product, int>();
            foreach (var product in NonDeletedAvailableProducts) freq.Add(product, 0);
            foreach (var op in OrderedProducts)
            {
                var product = op.Product;
                if (product.DeletedDateTime == null && product.SKU > 0)
                {
                    freq.TryAdd(product, 0);
                    freq[product]++;
                }
            }

            IEnumerable<Product> orderedByMostOrderes = NonDeletedAvailableProducts.OrderByDescending(p => freq[p]);
            IEnumerable<Product> productsOnSale = orderedByMostOrderes.Where(p => p.SalePercent > 0);
            IEnumerable<Product> productsAddedLastWeek = NonDeletedAvailableProducts.OrderByDescending(p => DateTime.Now - p.CreatedDateTime);
            ViewBag.CategoriesNames = GetCategoriesNames();

            return View((orderedByMostOrderes, productsOnSale, productsAddedLastWeek));
        }

        public IActionResult Search(string keyWord, string categoryName = "All", bool includeOutOfStock = false, bool includeDeleted = false)
        {
            Category category = _db.Categories.FirstOrDefault(c => c.Name == categoryName);
            // Errors
            if (categoryName != "All" && category == null)
            {
                TempData["fail"] = "Invalid Category!";
                return RedirectToAction("Home");
            }

            // Implementation
            IEnumerable<Product> Products = _db.Products.Include(p => p.Category);

            if (!string.IsNullOrEmpty(keyWord))
            {
                Products = Products.Where(Product => Product.Name.Contains(keyWord, StringComparison.OrdinalIgnoreCase) ||
                                            Product.Description.Contains(keyWord, StringComparison.OrdinalIgnoreCase));
                if (Products.Any())
                {
                    TempData["info"] = $"{Products.Count()} results For \"{keyWord}\"";
                }
                else
                {
                    TempData["fail"] = $"No Products Found For \"{keyWord}\"!";
                }
            }

            // Filter by category if provided
            if (categoryName != "All")
            {
                Products = Products.Where(p => p.Category.Name == categoryName);
            }

            // Filter by deleted if included
            if (includeDeleted)
            {
                if (!User.IsInRole("Admin") && !User.IsInRole("Moderator"))
                    return Forbid(); // If user is not authorized, return 403 Forbidden
            }
            else
            {
                Products = Products.Where(p => p.DeletedDateTime == null);
            }

            // Filter by in stock
            if (!includeOutOfStock)
            {
                Products = Products.Where(p => p.SKU > 0);
            }

            // Add Search to db
            if (keyWord != null)
            {
                _db.Searches.Add(new()
                {
                    User = _db.Users.FirstOrDefault(u => u.UserName == User.Identity.Name),
                    Category = category,
                    KeyWord = keyWord
                });
                _db.SaveChanges();
            }

            TempData["Categories"] = GetCategoriesNames();
            TempData["KeyWord"] = keyWord;
            TempData["Category"] = categoryName;
            TempData["includeOutOfStock"] = includeOutOfStock;
            TempData["IncludeDeleted"] = includeDeleted;

            return View(Products);
        }


        [Route("{id}")]
        public IActionResult FullView(int Id)
        {
            var Product = _db.Products
                .Include(p => p.Category)
                .Include(p => p.Reviews)
                    .ThenInclude(r => r.Reviewer)
                .FirstOrDefault(p => p.Id == Id);

            if (Product == null) return NotFound();

            // Increase product views
            Product.Views++;
            _db.Products.Update(Product);
            _db.SaveChanges();

            // product media
            string path = Path.Combine("wwwroot\\Media\\ProductMedia\\", Product.Id.ToString());
            string[] fileNames = Directory.GetFiles(path);
            List<string> fileNamesOnly = fileNames.Select(filePath => Path.GetFileName(filePath)).ToList();
            ViewBag.FileNames = fileNamesOnly;
            ViewBag.inCart = false;
            ViewBag.inWishlist = false;
            ViewBag.reviewed = true;
            if (User.Identity.IsAuthenticated)
            {
                var user = _db.Users
                    .Include(u => u.Cart)
                        .ThenInclude(c => c.CartProducts)
                    .Include(u => u.WishList)
                    .FirstOrDefault(u => u.UserName == User.Identity.Name);

                var cart = user.Cart;
                var wishList = user.WishList;
                var reviews = Product.Reviews;
                if (user.Cart.CartProducts != null && cart.CartProducts.Any(cp => cp.Product == Product))
                {
                    ViewBag.inCart = true;
                }
                if (user.WishList != null && wishList.Contains(Product))
                {
                    ViewBag.inWishlist = true;
                }
                if (!Product.Reviews.Any(rev => rev.Reviewer == user && rev.DeletedDateTime == null))
                {
                    ViewBag.reviewed = false;
                }
            }
            return View(Product);
        }

        [Authorize]
        public IActionResult IndexWishlist()
        {
            var user = _db.Users.Include(u => u.WishList).FirstOrDefault(u => u.UserName == User.Identity!.Name);

            return View(user!.WishList);
        }

        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult ModifyWishlist(int ProductId)
        {
            var user = _db.Users.FirstOrDefault(u => u.UserName == User.Identity.Name);
            var product = _db.Products.Find(ProductId);

            if (user.WishList.IsNullOrEmpty())
            {
                user.WishList = [product];
            }
            if (!user.WishList.Remove(product))
            {
                user.WishList.Add(product);
            }
            _db.Users.Update(user);
            _db.SaveChanges();

            return RedirectToAction(nameof(FullView), new { Id = ProductId });
        }


        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult AddReview(int ProductId, byte rating, string text)
        {
            var product = _db.Products.Find(ProductId);

            if (product == null) return NotFound();

            var user = _db.Users
                .Include(u => u.Orders)
                    .ThenInclude(o => o.OrderProducts)
                        .ThenInclude(op => op.Product)
                .FirstOrDefault(u => u.UserName == User.Identity.Name);

            if (product.Reviews.Any(r => r.Reviewer == user && r.DeletedDateTime == null))
            {
                TempData["info"] = "You can only post one review for the same product!";
            }
            else if (!user.Orders.SelectMany(o => o.OrderProducts).Any(op => op.Product.Id == ProductId))
            {
                TempData["info"] = "Can't review a product you didn't buy!";
            }
            else
            {
                Review review = new()
                {
                    Reviewer = user,
                    Rating = rating,
                    Text = text,
                };
                _db.Reviews.Add(review);
                product.Reviews.Add(review);
                _db.Products.Update(product);
                _db.SaveChanges();
            }

            return RedirectToAction(nameof(FullView), new { Id = ProductId });
        }

        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult EditReview(int productId, byte rating, string text)
        {
            var user = _db.Users.FirstOrDefault(u => u.UserName == User.Identity.Name);
            var review = _db.Reviews.FirstOrDefault(rev => rev.Reviewer == user && rev.ProductId == productId);
            if (review == null) return NotFound();
            
            if(review.Rating != rating) review.Rating = rating;
            if(review.Text != text) review.Text = text;
            _db.Reviews.Update(review);
            _db.SaveChanges(user);

            return RedirectToAction(nameof(FullView), new { Id = review.ProductId });
        }

        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult DeleteReview(int ReviewId)
        {
            var review = _db.Reviews.Find(ReviewId);
            if (review == null) return NotFound();

            review.DeletedDateTime = DateTime.Now;
            _db.Reviews.Update(review);
            _db.SaveChanges(_db.Users.FirstOrDefault(u => u.UserName == User.Identity.Name));

            return RedirectToAction(nameof(FullView), new { Id = review.ProductId });
        }
    }
}