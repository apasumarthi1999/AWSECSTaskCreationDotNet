using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Amazon;
using Amazon.CloudWatchLogs.Model;
using Amazon.ECS;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Configuration;

namespace AWSECSTaskCreationDotNet
{
   class DockerPushProgress : IProgress<JSONMessage>
   {
      public void Report( JSONMessage value )
      {
         if ( value.Progress == null )
            Console.WriteLine( $"Progress {value.Status}" );
         else
            Console.WriteLine( $"Progress. Status {value.Status}, ID {value.ID}, Current {value.Progress.Current}, Total {value.Progress.Total}" );
      }
   }

   class Program
   {
      static async Task Main( string[] args )
      {
         var configBuilder = new ConfigurationBuilder().AddJsonFile( "appsettings.json" );
         var config = configBuilder.Build();

         Console.WriteLine( "Please enter a docker image name from your local docker repository." );
         var dockerImageName = Console.ReadLine();

         var repoUri = await CreateECRRepositoryAsync( config, dockerImageName );
         var (taskIP, taskArn) = await CreateAndRunTaskAsync( config, repoUri );
         await TestEchoApiAsync( config, taskIP, taskArn );
      }

      // Create ECR repository from the given docker image
      static async Task<string> CreateECRRepositoryAsync( IConfiguration config, string dockerImageName )
      {
         Amazon.ECR.AmazonECRClient client = new Amazon.ECR.AmazonECRClient(
                                 config["ECRApiKey"],
                                 config["ECRApiSecret"],
                                 RegionEndpoint.GetBySystemName( config["ECRRegion"] ) );

         // Create a repository in the AWS ECR (Elastic Container Registry)
         var response = await client.CreateRepositoryAsync( new Amazon.ECR.Model.CreateRepositoryRequest()
         {
            RepositoryName = $"packages/{dockerImageName}-{Guid.NewGuid().ToString( "N" )}"
         } );

         if ( response.HttpStatusCode != System.Net.HttpStatusCode.OK )
         {
            Console.WriteLine( $"Failed to create repository in AWS ECR. Status {response.HttpStatusCode}" );
            return null;
         }

         Console.WriteLine( $"Repository created. Uri {response.Repository.RepositoryUri}" );

         var packageUri = response.Repository.RepositoryUri;

         // Generate access token to push your local docker image to the newly created repository on ECR
         Console.WriteLine( "Generating access token to push the docker image..." );
         var authResponse = await client.GetAuthorizationTokenAsync( new Amazon.ECR.Model.GetAuthorizationTokenRequest()
         {
         } );

         if ( authResponse.HttpStatusCode != System.Net.HttpStatusCode.OK )
         {
            Console.WriteLine( $"Failed to generate access token for AWS ECR. Status {authResponse.HttpStatusCode}" );
            return null;
         }

         var accessToken = Encoding.UTF8.GetString( Convert.FromBase64String( authResponse.AuthorizationData.First().AuthorizationToken ) ).Split( ":" )[1];

         // Create the local docker client
         DockerClient dockerClient = new DockerClientConfiguration().CreateClient();

         // Associate the remote ECR repository Uri as tag to the local docker image
         Console.WriteLine( "Associating remote repository uri with given local docker image..." );
         await dockerClient.Images.TagImageAsync( dockerImageName, new ImageTagParameters()
         {
            RepositoryName = packageUri,
            Force = true
         } );

         // Push the docker image to the remote ECR repository
         await dockerClient.Images.PushImageAsync( packageUri, new ImagePushParameters()
         {
            Tag = "latest"
         }, new AuthConfig()
         {
            Username = "AWS",
            Password = accessToken,
            ServerAddress = authResponse.AuthorizationData.First().ProxyEndpoint
         }, new DockerPushProgress() );

         Console.WriteLine( "Docker push completed...press Enter to continue..." );
         Console.ReadLine();

         return packageUri;
      }

      // Create ECS log group, task definition, container definition, run the task in the container and
      // get the public IP of the task container
      private static async Task<(string, string)> CreateAndRunTaskAsync( IConfiguration config, string repoUri )
      {
         Amazon.CloudWatchLogs.AmazonCloudWatchLogsClient logClient = new Amazon.CloudWatchLogs.AmazonCloudWatchLogsClient(
                                 config["ECSApiKey"],
                                 config["ECSApiSecret"],
                                 RegionEndpoint.GetBySystemName( config["ECRRegion"] ) );

         var logGroupsResponse = await logClient.DescribeLogGroupsAsync( new DescribeLogGroupsRequest()
         {
            LogGroupNamePrefix = "/ecs/task-echo-api"
         } );

         if ( ( logGroupsResponse.LogGroups == null ) || ( logGroupsResponse.LogGroups.FirstOrDefault( lg => string.Compare( lg.LogGroupName, $"/ecs/task-echo-api", true ) == 0 ) == null ) )
         {
            var logResponse = await logClient.CreateLogGroupAsync( new Amazon.CloudWatchLogs.Model.CreateLogGroupRequest()
            {
               LogGroupName = $"/ecs/task-echo-api"
            } );

            Console.WriteLine( $"Log Group creation status {logResponse.HttpStatusCode}" );
         }
         else
         {
            Console.WriteLine( "Log group exists...proceeding..." );
         }

         // Create a task definition using docker image pushed to our AWS ECR repository
         AmazonECSClient ecsClient = new AmazonECSClient(
                                 config["ECSApiKey"],
                                 config["ECSApiSecret"],
                                 RegionEndpoint.GetBySystemName( config["ECRRegion"] ) );

         var taskResponse = await ecsClient.RegisterTaskDefinitionAsync( new Amazon.ECS.Model.RegisterTaskDefinitionRequest()
         {
            RequiresCompatibilities = new List<string>() { "FARGATE" },
            TaskRoleArn = "ecsTaskExecutionRole",
            ExecutionRoleArn = "ecsTaskExecutionRole",
            Cpu = "256",
            Memory = "512",
            NetworkMode = NetworkMode.Awsvpc,
            Family = $"task-echo-api",
            ContainerDefinitions = new List<Amazon.ECS.Model.ContainerDefinition>()
            {
               new Amazon.ECS.Model.ContainerDefinition()
               {
                  Name = $"task-container-echo-api",
                  Image = repoUri,
                  Cpu = 256,
                  Memory = 512,
                  Essential = true,
                  LogConfiguration = new Amazon.ECS.Model.LogConfiguration()
                  {
                     LogDriver = LogDriver.Awslogs,
                     Options = new Dictionary<string, string>()
                     {
                        { "awslogs-group", $"/ecs/task-echo-api" },
                        { "awslogs-region", config["ECRRegion"] },
                        { "awslogs-stream-prefix", "ecs" }
                     }
                  }
               }
            }
         } );

         Console.WriteLine( $"Task creation status {taskResponse.HttpStatusCode}" );

         Amazon.ECS.Model.Task task = null;

         try
         {
            var response = await ecsClient.RunTaskAsync( new Amazon.ECS.Model.RunTaskRequest()
            {
               Cluster = config["Cluster"],
               Count = 1,
               LaunchType = LaunchType.FARGATE,
               TaskDefinition = $"task-echo-api",
               NetworkConfiguration = new Amazon.ECS.Model.NetworkConfiguration()
               {
                  AwsvpcConfiguration = new Amazon.ECS.Model.AwsVpcConfiguration()
                  {
                     Subnets = config["Subnet"].Split( ',' ).ToList(),
                     AssignPublicIp = AssignPublicIp.ENABLED,
                     SecurityGroups = new List<string> { config["SecurityGroup"] }
                  }
               },
               Overrides = new Amazon.ECS.Model.TaskOverride()
               {
                  ContainerOverrides = new List<Amazon.ECS.Model.ContainerOverride>()
                  {
                     new Amazon.ECS.Model.ContainerOverride()
                     {
                        Name = $"task-container-echo-api"
                     }
                  }
               }
            } );

            if ( response.HttpStatusCode != System.Net.HttpStatusCode.OK )
               throw new Exception( response.HttpStatusCode.ToString() );

            if ( response.Tasks == null )
               throw new Exception( "Failed to create task" );

            task = response.Tasks.FirstOrDefault();

            if ( task == null )
               throw new Exception( "Failed to create task" );

            var taskArn = task.TaskArn;

            while ( true )
            {
               var responseTask = await ecsClient.DescribeTasksAsync( new Amazon.ECS.Model.DescribeTasksRequest()
               {
                  Cluster = config["Cluster"],
                  Tasks = new List<string> { taskArn },
               } );

               task = responseTask.Tasks?.FirstOrDefault();

               if ( task != null && string.Compare( task.LastStatus, "RUNNING", true ) == 0 )
               {
                  Console.WriteLine( $"Task is running..." );
                  break;
               }
               else if ( task != null &&
                  ( string.Compare( task.LastStatus, "STOPPED", true ) == 0 || string.Compare( task.LastStatus, "STOPPING", true ) == 0 ) )
               {
                  throw new Exception( $"Failed to start the task [task-arn = {taskArn}]" );
               }

               await Task.Delay( 5000 );
            }

            string ipv4Addr = task.Containers.FirstOrDefault().NetworkInterfaces.FirstOrDefault().PrivateIpv4Address;

            var ec2Client = new Amazon.EC2.AmazonEC2Client(
                                 config["ECSApiKey"],
                                 config["ECSApiSecret"],
                                 RegionEndpoint.GetBySystemName( config["ECRRegion"] ) );
            var describeNetObj = new Amazon.EC2.Model.DescribeNetworkInterfacesRequest();
            describeNetObj.Filters.Add( new Amazon.EC2.Model.Filter()
            {
               Name = "private-ip-address",
               Values = new List<string> { ipv4Addr }
            } );
            var ec2Response = ec2Client.DescribeNetworkInterfacesAsync( describeNetObj ).Result;
            string taskIP = ec2Response.NetworkInterfaces.FirstOrDefault().Association.PublicIp;

            return (taskIP, task.TaskArn);
         }
         catch ( Exception ex )
         {
            Console.WriteLine( ex.ToString() );
            throw;
         }
      }

      // Test the echo api from the public uri of the running ECS task
      private static async Task TestEchoApiAsync( IConfiguration config, string taskIP, string taskArn )
      {
         Console.WriteLine( "Enter a message to echo..." );
         var message = Console.ReadLine();
         var httpClient = new HttpClient();
         var echoedMessage = await httpClient.GetStringAsync( Uri.EscapeUriString( $"http://{taskIP}:5000/echo/{message}" ) );
         Console.WriteLine( $"Echoed Message: {echoedMessage}" );

         AmazonECSClient ecsClient = new AmazonECSClient(
                        config["ECSApiKey"],
                        config["ECSApiSecret"],
                        RegionEndpoint.GetBySystemName( config["ECRRegion"] ) );

         await ecsClient.StopTaskAsync( new Amazon.ECS.Model.StopTaskRequest()
         {
            Cluster = config["Cluster"],
            Task = taskArn,
            Reason = "User Stop"
         } );

         Console.WriteLine( "Stopped Task. Press Enter to quit..." );
         Console.ReadLine();
      }
   }
}
