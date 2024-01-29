using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Xml.Linq;

class Program
{
    private static IConfiguration configuration;
    private static XElement referenceData;

    static void Main()
    {
        using IHost host = CreateHostBuilder().Build();

        // Load configuration settings from appsettings.json
        configuration = host.Services.GetRequiredService<IConfiguration>();

        // Load reference data
        referenceData = XDocument.Load("ReferenceData.xml").Root.Element("Factors");

        // Watch for new XML files in the input folder
        var fileWatcher = new FileSystemWatcher(configuration["InputFolder"], "*.xml");
        fileWatcher.Created += (sender, e) => WaitForFileAndProcess(e.FullPath, configuration["OutputFolder"]);
        fileWatcher.EnableRaisingEvents = true;

        Console.WriteLine("Press 'Q' to quit.");
        while (Console.ReadKey().Key != ConsoleKey.Q) { }
    }

    static IHostBuilder CreateHostBuilder() =>
        Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration((hostContext, config) =>
            {
                config.AddJsonFile("config.json", optional: false, reloadOnChange: true);
            });

    static void WaitForFileAndProcess(string filePath, string outputFolderPath)
    {
        const int maxRetries = 10;
        const int delayMilliseconds = 500;

        for (int retry = 1; retry <= maxRetries; retry++)
        {
            try
            {
                // Wait for a short delay before attempting to process the XML file
                Thread.Sleep(delayMilliseconds);

                // Attempt to process the XML file
                ProcessXmlFile(filePath, outputFolderPath);
                return; // Successfully processed, exit the loop
            }
            catch (IOException ex) when (retry <= maxRetries)
            {
                // Retry if IOException occurs
                Console.WriteLine($"Retry {retry} - {ex.Message}");
                Thread.Sleep(delayMilliseconds);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing XML file: {ex.Message}");
                return; // Exit the loop on other exceptions
            }
        }

        Console.WriteLine($"File '{filePath}' is still in use after {maxRetries} retries. Skipping.");
    }

    static void ProcessXmlFile(string inputFilePath, string outputFolderPath)
    {
        try
        {
            // Load the input XML file
            XDocument inputXml = XDocument.Load(inputFilePath);

            // Process the XML data and create the output XML
            XDocument outputXml = ProcessData(inputXml);

            // Save the result to the output folder
            string outputFileName = Path.Combine(outputFolderPath, Path.GetFileNameWithoutExtension(inputFilePath) + "-Result.xml");
            outputXml.Save(outputFileName);

            Console.WriteLine($"Processing complete. Result saved to: {outputFileName}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error processing XML file: {ex.Message}");
        }
    }

    static XDocument ProcessData(XDocument inputXml)
    {
        // In this part there is a problem with correct calculations
        //Since there is no time for the deadline it is not fixed yet


        // Load factors from reference data
        var valueFactors = referenceData.Element("ValueFactor");
        var emissionFactors = referenceData.Element("EmissionsFactor");

        // Perform data processing based on the XML structure and calculation instructions
        var outputXml = new XDocument(
            new XElement("GenerationOutput",
                new XElement("Totals",
                    inputXml.Descendants("WindGenerator")
                            .Union(inputXml.Descendants("GasGenerator"))
                            .Union(inputXml.Descendants("CoalGenerator"))
                            .GroupBy(generator => generator.Element("Name")?.Value)
                            .Where(group => group.Key != null)
                            .Select(group =>
                            {
                                string generatorType = group.First().Name.LocalName;
                                double valueFactor = GetFactor(generatorType, valueFactors);
                                double emissionFactor = GetFactor(generatorType, emissionFactors);
                            
                                return new XElement("Generator",
                                    new XElement("Name", group.Key),
                                    new XElement("Total",
                                        group.SelectMany(generator => generator.Descendants("Day"))
                                             .Where(day => day.Element("Energy") != null && day.Element("Price") != null)
                                             .Select(day =>
                                                Convert.ToDouble(day.Element("Energy").Value) *
                                                Convert.ToDouble(day.Element("Price").Value) *
                                                valueFactor
                                             ).Sum()
                                    )
                                );
                            })
                ),
                new XElement("MaxEmissionGenerators",
                    inputXml.Descendants("GasGenerator")
                            .Union(inputXml.Descendants("CoalGenerator"))
                            .Where(generator => generator.Element("EmissionsRating") != null)
                            .Select(generator =>
                            {
                                string generatorType = generator.Name.LocalName;
                                double emissionRating = Convert.ToDouble(generator.Element("EmissionsRating").Value);
                                double emissionFactor = GetFactor(generatorType, emissionFactors);
                
                                var maxDailyEmission = generator.Descendants("Day")
                                                              .Select(day => new
                                                              {
                                                                  Date = day.Element("Date").Value,
                                                                  Emission = Convert.ToDouble(day.Element("Energy").Value) *
                                                                             emissionRating *
                                                                             emissionFactor
                                                              })
                                                              .OrderByDescending(day => day.Emission)
                                                              .First();
                
                                return new XElement("Day",
                                    new XElement("Name", generator.Element("Name").Value),
                                    new XElement("Date", maxDailyEmission.Date),
                                    new XElement("Emission", maxDailyEmission.Emission)
                                );
                            })
                ),
                new XElement("ActualHeatRates",
                    inputXml.Descendants("CoalGenerator")
                            .Where(generator => generator.Element("TotalHeatInput") != null && generator.Element("ActualNetGeneration") != null)
                            .Select(generator =>
                            {
                                double totalHeatInput = Convert.ToDouble(generator.Element("TotalHeatInput").Value);
                                double actualNetGeneration = Convert.ToDouble(generator.Element("ActualNetGeneration").Value);
                                double actualHeatRate = totalHeatInput / actualNetGeneration;

                                return new XElement("ActualHeatRate",
                                    new XElement("Name", generator.Element("Name").Value),
                                    new XElement("HeatRate", actualHeatRate)
                                );
                            })
                )
            )
        );

        return outputXml;
    }


    static double GetFactor(string generatorType, XElement factorElement)
    {
        if (generatorType != null && factorElement != null)
        {
            XElement factor;

            switch (generatorType.ToLower())
            {
                case "offshore":
                    factor = factorElement.Element("Low");
                    break;
                case "onshore":
                    factor = factorElement.Element("High");
                    break;
                case "gas":
                    factor = factorElement.Element("Medium");
                    break;
                case "coal":
                    factor = factorElement.Element("Medium");
                    break;
                default:
                    factor = null;
                    break;
            }

            return factor != null ? Convert.ToDouble(factor.Value) : 0.0;
        }

        return 0.0;
    }
}
