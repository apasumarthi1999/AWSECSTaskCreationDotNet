using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace EchoApi.Controllers
{
   [ApiController]
   [Route( "[controller]" )]
   public class EchoController : ControllerBase
   {
      private readonly ILogger<EchoController> _logger;

      public EchoController( ILogger<EchoController> logger )
      {
         _logger = logger;
      }

      [HttpGet( "{message}" )]
      public string Echo( string message )
      {
         _logger?.LogInformation( $"Echor back message...{message}" );
         return message;
      }
   }
}
