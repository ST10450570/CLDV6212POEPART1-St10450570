using System.ComponentModel.DataAnnotations;

namespace ABCRetails.Models.ViewModels
{
    public class OrderEditViewModel
    {
        public Order Order { get; set; } = new Order();
        public List<Customer> Customers { get; set; } = new List<Customer>();
        public List<Product> Products { get; set; } = new List<Product>();
        public List<string> StatusOptions { get; set; } = new List<string>();
    }
}