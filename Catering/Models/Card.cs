using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Catering.Models
{
    public enum Card : int
    {
        Entre = 0,
        Drink = 1,
        Review = 2,
        ReviewAll = 3,
        Confirmation = 4,
        OkWithString = 5,
        OkWithCard = 6, // refreshes to a blank card, need to make a real card for this
        LoginRequest = 7,
        ThrottleWarning = 8, // got a 500 from directline??
        Teapot = 9, // Got a 502 back from directline
        Error = 10,
        ErrMenu = 11
    }
}
