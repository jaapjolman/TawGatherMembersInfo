﻿using HtmlAgilityPack;
using Neitri;
using Neitri.WebCrawling;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using TawGatherMembersInfo.Models;

namespace TawGatherMembersInfo
{
	public class WebDataParser
	{
		[Dependency]
		DbContextProvider db;

		public async Task UpdateUnitContents(SessionMannager sessionManager, int tawUnitId)
		{
			await new UpdateUnitContentsHandler(db).Run(sessionManager, tawUnitId);
		}

		class UpdateUnitContentsHandler
		{
			DbContextProvider db;
			Dictionary<string, List<UnitRoasterPersonLine>> personNameToPersonLines = new Dictionary<string, List<UnitRoasterPersonLine>>();

			public UpdateUnitContentsHandler(DbContextProvider db)
			{
				this.db = db;
			}

			public async Task Run(SessionMannager sessionManager, int tawUnitId)
			{
				var newVersion = new Random().Next(); // all person to unit relations without this unit version will be deleted

				Log.Trace($"getting unit taw id:{tawUnitId} roaster");
				var url = Unit.GetUnitRoasterPage(tawUnitId);
				var session = await sessionManager.GetAsync();
				var response = session.Value.GetUrl(url);
				session.Dispose();
				var html = response.HtmlDocument;
				Log.Trace($"got unit taw id:{tawUnitId} roaster");

				var roasterDiv = html.GetElementbyId("ctl00_bcr_UpdatePanel1").SelectSingleNode("./div/ul");

				Log.Trace("parsing unit roaster");
				await ParseUnitContents(roasterDiv, null);
				Log.Trace("finished parsing unit roaster");

				Log.Trace("parsing roaster people");
				var tasks = new List<Task>(personNameToPersonLines.Count);

				foreach (var kvp in personNameToPersonLines)
				{
					var personName = kvp.Key;
					var personLines = kvp.Value;
					var task = Task.Run(() =>
					{
						using (var data = db.NewContext)
						{
							Log.Trace("parsing & saving roaster person:" + personName);
							foreach (var personLine in personLines)
							{
								personLine.FinishParsing(data);
							}
							var personUnitIds = personLines.Select(p => p.PersonToUnitId).ToArray();
							// if some person to unit is still valid, and not one of those we just updated, mark it as not valid anymore
							data
								.People
								.First(p => p.Name == personName)
								.Units
								.Where(u => u.Removed == DateTime.MinValue) // still valid, not removed
								.Where(u => !personUnitIds.Contains(u.PersonUnitId)) // except those we found & updated
								.ForEach(u => u.Removed = DateTime.UtcNow); // remove it

							data.SaveChanges();
							Log.Trace("done parsing & saving roaster person:" + personName);
						}
					});
					tasks.Add(task);
				};
				await Task.WhenAll(tasks.ToArray());

				Log.Trace("finished parsing roaster people");
			}

			class UnitRoasterPersonLine
			{
				public string PersonName => name;

				public long PersonToUnitId { get; private set; }

				string name = "unnamed";
				string rank = "";
				string positionNameLong = "";
				string positionNameShort = "";
				bool onLeave = false;
				long unitId;

				public UnitRoasterPersonLine(string text, long unitId)
				{
					this.unitId = unitId;
					var dashIndex = text.LastIndexOf("-");

					if (dashIndex != -1)
					{
						var part1 = text.Substring(0, dashIndex - 1).Trim();
						var part2 = text.Substring(dashIndex + 1).Trim();

						if (part2.ToLower().Contains("on leave"))
						{
							onLeave = true;
							var parts = part1.Split(',');
							name = parts[0].Trim();
							rank = parts[1].Trim();
						}
						else
						{
							positionNameLong = part1;
							var parts = part2.Split(',');
							name = parts[0].Trim();
							rank = parts[1].Trim();
						}
					}
					else
					{
						var parts = text.Split(',');
						name = parts[0].Trim();
						rank = parts[1].Trim();
					}

					if (positionNameLong != "")
					{
						positionNameShort = Person.positionNameShortToPositionNameLong.Reverse.GetValue(positionNameLong, null);
						if (positionNameShort == null) Log.Error("cannot find positionNameShortToPositionNameLong.Reverse[" + positionNameLong + "]");
					}
				}

				public void FinishParsing(MyDbContext data)
				{
					/*
						text use cases:
						Commander-in-Chief - DOC, GEN5
						Commanding Officer - Constance, CPT
						Executive Officer - Deceded, LTC
						BetaHook, PFC - On Leave
						Guthrie, PFC - On Leave
						Constance, CPT
						Juvenis, COL
					*/

					var person = GetPersonFromName(data, name, rank);
					if (onLeave) person.Status = "on leave";

					var personToUnit = data.PersonUnits.FirstOrDefault(p => p.ForPerson.PersonId == person.PersonId && p.ForUnit.UnitId == unitId);
					if (personToUnit == null)
					{
						personToUnit = new PersonUnit();
						personToUnit.ForPerson = person;
						personToUnit.ForUnit = data.Units.Find(unitId);
						personToUnit = data.PersonUnits.Add(personToUnit);
					}
					personToUnit.PositionNameShort = positionNameShort;
					personToUnit.Joined = DateTime.UtcNow;
					personToUnit.Removed = DateTime.MinValue;

					PersonToUnitId = personToUnit.PersonUnitId;

					data.SaveChanges();
				}
			}

			async Task ParseUnitContents(HtmlNode unitNamePlusUl, long? parentUnitId)
			{
				List<Task> tasks;
				using (var data = db.NewContext)
				{
					var unitTypeNameElement = unitNamePlusUl.SelectSingleNode("li | span");
					var unitTypeA = unitTypeNameElement.SelectSingleNode("*/a[1] | a[1]");
					var unitNameA = unitTypeNameElement.SelectSingleNode("*/a[2] | a[2]");

					var type = unitTypeA.InnerText;
					var tawId = int.Parse(unitNameA.GetAttributeValue("href", "/unit/-1.aspx").TakeStringBetweenLast("/", ".aspx"));
					var name = unitNameA.InnerText;

					Log.Trace("parsing unit roaster, taw unit id: " + tawId);

					var unit = GetUnit(data, tawId, name);
					unit.Type = type;
					if (parentUnitId.HasValue) unit.ParentUnit = data.Units.Find(parentUnitId.Value);

					data.SaveChanges();

					var children = unitNamePlusUl.SelectSingleNode("ul");

					tasks = new List<Task>(children.ChildNodes.Count);

					foreach (var child in children.ChildNodes)
					{
						var personA = child.SelectSingleNode("a");
						if (personA != null)
						{
							// person
							var text = child.InnerText;
							//tasks.Add(Task.Run(() => ParsePersonFromUnitRoaster(text, unit.Id)));
							var personLine = new UnitRoasterPersonLine(text, unit.UnitId);

							lock (personNameToPersonLines)
							{
								List<UnitRoasterPersonLine> personLines;
								if (!personNameToPersonLines.TryGetValue(personLine.PersonName, out personLines))
								{
									personNameToPersonLines[personLine.PersonName] = personLines = new List<UnitRoasterPersonLine>();
								}
								personLines.Add(personLine);
							}
						}
						else
						{
							// unit
							tasks.Add(Task.Run(() => ParseUnitContents(child, unit.UnitId)));
						}
					}
				}
				await Task.WhenAll(tasks.ToArray());
			}
		}

		static Person GetPersonFromName(MyDbContext data, string name, string rankNameShort = null)
		{
			var person = data.People.FirstOrDefault(p => p.Name == name);
			if (person == null)
			{
				person = new Person();
				person.Name = name;
				person = data.People.Add(person);
			}
			if (!rankNameShort.IsNullOrEmpty() && (person.Ranks == null || person.Rank?.NameShort != rankNameShort))
			{
				var personRank = new PersonRank();
				personRank.ForPerson = person;
				personRank.NameShort = rankNameShort;
				personRank.ValidFrom = DateTime.MinValue;
				person.Ranks = new List<PersonRank>() { personRank };
			}
			return person;
		}

		static Unit GetUnit(MyDbContext data, int unitTawId, string name)
		{
			var unit = data.Units.FirstOrDefault(u => u.TawId == unitTawId);
			if (unit == null)
			{
				unit = new Unit();
				unit.TawId = unitTawId;
				unit = data.Units.Add(unit);
			}
			unit.Name = name;
			return unit;
		}

		class DossierMovements
		{
			public List<DossierMovement> Movements;

			public class DossierMovement
			{
				public string id;
				public string timestamp;
				public string description;
			}
		}

		public async Task UpdateInfoFromProfilePage(SessionMannager sessionManager, string personName)
		{
			var session = await sessionManager.GetAsync();

			Log.Trace("updating profile of " + personName + " start");

			var url = Person.GetPersonProfilePageUrl(personName);
			var response = session.Value.GetUrl(url);
			var html = response.HtmlDocument;

			Log.Trace("updating profile of " + personName + " got web response");

			using (var data = db.NewContext)
			{
				var person = data.People.FirstOrDefault(p => p.Name == personName);
				if (person == null)
				{
					Log.Error("person with name " + personName + " was not found in database");
					return;
				}

				// steam profile id
				var steamProfileLinkPrefix = "http://steamcommunity.com/profiles/";
				var steamProfileLinkElement = html.GetElementbyId("hfSteam");
				if (steamProfileLinkElement != null)
				{
					var steamProfileLink = steamProfileLinkElement.GetAttributeValue("href", steamProfileLinkPrefix + "-1");
					var steamId = long.Parse(steamProfileLink.Substring(steamProfileLinkPrefix.Length));
					person.SteamId = steamId;
				}

				// avatar image
				var avatarElement = html.DocumentNode.SelectSingleNode("//*[@class='dossieravatar']/img");
				if (avatarElement != null)
				{
					var avatarImageLink = avatarElement.GetAttributeValue("src", null);
					if (avatarImageLink != null)
					{
						person.AvatarImageUrl = "http://taw.net" + avatarImageLink;
					}
				}

				// bio
				var biographyElement = html.DocumentNode.SelectSingleNode("//*[@id='dossierbio']");
				if (biographyElement != null)
				{
					var biography = biographyElement.InnerText.Trim();
					var bioTextHeader = "Bio:";
					if (biography.StartsWith(bioTextHeader)) biography = biography.Substring(bioTextHeader.Length);
					person.BiographyContents = biography;
				}

				var table = new HtmlTwoColsStringTable(html.DocumentNode.SelectNodes("//*[@class='dossiernexttopicture']/table//tr"));

				// country
				person.CountryName = table.GetValue("Location:", person.CountryName);
				person.Status = table.GetValue("Status:", person.Status).ToLower();
				{
					var joined = table.GetValue("Joined:", "01-01-0001"); // 10-03-2014  month-day-year // wtf.. americans...
					var joinedParts = joined.Split('-');
					person.DateJoinedTaw = new DateTime(
						int.Parse(joinedParts[2]),
						int.Parse(joinedParts[0]),
						int.Parse(joinedParts[1])
					);
				}

				person.LastProfileDataUpdatedDate = DateTime.UtcNow;
				person.ClearCache();

				// dossier movements
				// rank in time
				// position in unit in time
				{
					var res = await session.Value.PostJsonAsync("http://taw.net/services/JSONFactory.asmx/GetMovement", new { callsign = person.Name });
					session.Dispose();
					var d = (string)JObject.Parse(res)["d"];
					var dossierMovements = JsonConvert.DeserializeObject<DossierMovements>(d);

					foreach (var dossierMovement in dossierMovements.Movements)
					{
						var timestamp = ParseMonthDayYear(dossierMovement.timestamp);
						var tawId = long.Parse(dossierMovement.id);
						var description = dossierMovement.description;

						if (description.Contains("applied for TAW.")) person.AppliedForTaw = timestamp;
						else if (description.Contains("was admitted to TAW")) person.AdmittedToTaw = timestamp;
						else if (description.Contains("was promoted to"))
						{
							if (person.Ranks == null || (person.Ranks.Count == 1 && person.Ranks.First().ValidFrom == DateTime.MinValue))
								person.Ranks = new List<PersonRank>();

							if (!person.Ranks.Any(r => r.TawId == tawId))
							{
								// aeroson was promoted to Sergeant by <a href="/member/Samblues.aspx">Samblues</a>.
								// aeroson was promoted to Private First Class by <a href="/member/MaverickSabre.aspx">MaverickSabre</a>.
								var rankByWho = description.TakeStringAfter("was promoted to").Trim();
								var byWho = description.TakeStringAfter("by").TakeStringBefore("</a>").TakeStringAfterLast(">");
								while (byWho.EndsWith(".")) byWho = byWho.RemoveFromEnd(1).Trim();
								var rankNameLong = rankByWho.TakeStringBefore("by").Trim();

								var personRank = new PersonRank();
								personRank.NameLong = rankNameLong;
								personRank.ValidFrom = timestamp;
								personRank.ForPerson = person;
								personRank.ByWho = GetPersonFromName(data, byWho);
								personRank.TawId = tawId;
								person.Ranks.Add(personRank);
							}
						}
						else if (description.Contains("was joined to units"))
						{
							// aeroson was joined to units AM2 Charlie Squad by MaverickSabre.
							// aeroson was joined to units AM2 Charlie FT by Samblues.
							// <a href="/member/aeroson.aspx">aeroson</a> was joined to units <a href="/unit/3617.aspx">AM2 Charlie FT</a> by <a href="/member/Samblues.aspx">Samblues</a>.
						}
						else if (description.Contains("was removed from units"))
						{
							// aeroson was removed from units AM2 TI Office by MaverickSabre.
							// <a href="/member/aeroson.aspx">aeroson</a> was removed from units <a href="/unit/1549.aspx">AM2 TI Office</a> by <a href="/member/MaverickSabre.aspx">MaverickSabre</a>.
						}
						else if (description.Contains("was assigned to position"))
						{
							// aeroson was assigned to position Training Instructor in unit AM2 TI Office by MaverickSabre.
							// aeroson was assigned to position Squad Leader in unit AM2 Charlie Squad by MaverickSabre.
							// <a href="/member/aeroson.aspx">aeroson</a> was assigned to position Squad Leader in unit <a href="/unit/1505.aspx">AM2 Charlie Squad</a> by <a href="/member/MaverickSabre.aspx">MaverickSabre</a>.
						}
						else if (description.Contains("was removed from position"))
						{
							// aeroson was removed from position Training Instructor in unit AM2 TI Office by MaverickSabre.
							// <a href="/member/aeroson.aspx">aeroson</a> was removed from position Training Instructor in unit <a href="/unit/1549.aspx">AM2 TI Office</a> by MaverickSabre.
						}
						else if (description.Contains("was returned to active duty by"))
						{
							// <a href="/member/MaverickSabre.aspx">MaverickSabre</a> was returned to active duty by <a href="/member/Lucky.aspx">Lucky</a>.
						}
						else if (description.Contains("was put on leave by"))
						{
							// <a href="/member/MaverickSabre.aspx">MaverickSabre</a> was put on leave by <a href="/member/Juvenis.aspx">Juvenis</a>.
						}
						else if (description.Contains("was discharged by"))
						{
							// <a href="/member/MaverickSabre.aspx">MaverickSabre</a> was discharged by <a href="/member/Lucid.aspx">Lucid</a>.
						}
						else if (description.Contains("was reinstated by"))
						{
							// <a href="/member/Dackey.aspx">Dackey</a> was reinstated by <a href="/member/Phenom.aspx">Phenom</a>
						}
						else if (description.Contains("was discharged by"))
						{
							// <a href="/member/MaverickSabre.aspx">MaverickSabre</a> was discharged by <a href="/member/Lucid.aspx">Lucid</a>.
						}
						else if (description.Contains("was discharged honorable by"))
						{
							// <a href="/member/Xsage.aspx">Xsage</a> was discharged honorable by <a href="/member/TexasHillbilly.aspx">TexasHillbilly</a>.
						}
						else if (description.Contains("was discharged dishonorable by"))
						{
							// <a href="/member/Dackey.aspx">Dackey</a> was discharged dishonorable by <a href="/member/Juvenis.aspx">Juvenis</a>.
						}
						else if (description.Contains("Unknown was removed from unit Unknown by"))
						{
							// removed person from removes unit
						}
						else
						{
							Log.Warn("unexpected dossier row: " + description);
						}
					}
				}
				data.SaveChangesAsync();
			}

			Log.Trace("done updating profile of " + personName + ", parsed and saved");
		}

		/// <summary>
		/// MM-dd-yyyy
		/// </summary>
		/// <param name="str"></param>
		/// <returns></returns>
		static DateTime ParseMonthDayYear([NotNull] string str)
		{
			try
			{
				return DateTime.ParseExact(str, "M-d-yyyy", CultureInfo.InvariantCulture);
			}
			catch
			{
				Log.Error($"failed to {nameof(ParseMonthDayYear)} DateTime:'{str}'");
				throw;
			}
		}

		static DateTime ParseUsTime([NotNull] string str)
		{
			str = str.Replace('/', '.');
			try
			{
				return DateTime.ParseExact(str, "M.d.yyyy H:mm:ss zzz", CultureInfo.InvariantCulture);
			}
			catch
			{
				Log.Error($"failed to {nameof(ParseUsTime)} DateTime:'{str}'");
				throw;
			}
			//return DateTime.Parse(str);
		}

		public enum ParseEventResult
		{
			ValidEvent,
			ErrorenousEvent,
			BaseEvent,
			InvalidUriProbablyLastEvent,
		}

		public async Task<ParseEventResult> ParseEventData(SessionMannager sessionManager, long eventTawId)
		{
			try
			{
				Log.Trace("parsing event data, taw id:" + eventTawId + " start");
				var url = Event.GetEventPage(eventTawId);
				var session = await sessionManager.GetAsync();
				var response = session.Value.GetUrl(url);
				session.Dispose();
				ParseEventResult result;
				Log.Trace("parsing event data, taw id:" + eventTawId + " got web response");
				using (var data = db.NewContext)
				{
					result = ParseEventData_1(data, response);
					data.SaveChanges();
				}
				Log.Trace("parsing event data, taw id:" + eventTawId + " parsed and saved");
				return result;
			}
			catch (Exception e)
			{
				Log.Error("ecountered errorenous event, taw id:" + eventTawId);
				Log.Error(e);
				return ParseEventResult.ErrorenousEvent;
			}
		}

		ParseEventResult ParseEventData_1(MyDbContext data, MyHttpWebResponse response)
		{
			var uriPath = response.ResponseUri.AbsolutePath;
			if (uriPath.Contains("event") == false)
			{
				return ParseEventResult.InvalidUriProbablyLastEvent;
				Log.Error("the event you are trying to parse has invalid uri");
			}

			var eventTawIdStr = uriPath.Split('/', '\\').Last().RemoveFromEnd(".aspx".Length);
			var eventTawId = int.Parse(eventTawIdStr);

			var htmlText = response.ResponseText;
			htmlText = htmlText?.TakeStringAfter("ctl00_ctl00_bcr_bcr_UpdatePanel\">");
			if (htmlText.Contains("This is a Base Event and should never be seen"))
			{
				Log.Trace("event " + eventTawId + " is invalid 'base event', skipping");
				return ParseEventResult.BaseEvent; // http://taw.net/event/65132.aspx
			}

			var evt = data.Events.FirstOrDefault(e => e.TawId == eventTawId);
			if (evt == null)
			{
				evt = new Event();
				evt.TawId = eventTawId;
				evt = data.Events.Add(evt);
			}
			ParseEventData_2(data, evt, htmlText);
			return ParseEventResult.ValidEvent;
		}

		void ParseEventData_2(MyDbContext data, Event evt, string htmlText)
		{
			// this page is so badly coded the HTML is invalid, chrome shows it correctly though, kudos to it
			// but HtmlAgilityPack just fails on it

			var eventInfoText = htmlText.TakeStringBetween("<table cellpadding=\"20\" cellspacing=\"5\">", "</table>");

			var eventInfoDoc = new HtmlDocument();
			eventInfoDoc.LoadHtml(eventInfoText);
			var eventInfo = new HtmlTwoColsStringTable(eventInfoDoc.DocumentNode);
			/*
			Name	GRAW Practice -- Saber Squad Thursday Night (NA-SA)
			Description	GRAW Practice -- Saber Squad Thursday Night (NA-SA)
			Type	Practice
			Unit	Ghost Recon
			When	From: 6/3/2016 04:00:00 +02:00 to: 6/3/2016 05:00:00 +02:00
			Mandatory	Yes
			Cancelled	No
			*/
			evt.Name = eventInfo["Name"];
			evt.Description = eventInfo["Description"];
			evt.Type = eventInfo["Type"];
			evt.Mandatory = eventInfo["Mandatory"] == "Yes";
			evt.Cancelled = eventInfo["Cancelled"] == "Yes";

			var when = eventInfo["When"];

			var strFrom = when.TakeStringBetween("from:", "to:", StringComparison.InvariantCultureIgnoreCase).Trim();
			if (strFrom != null) evt.From = ParseUsTime(strFrom);

			var strTo = when.TakeStringAfter("to:", StringComparison.InvariantCultureIgnoreCase).Trim();
			if (strTo != null) evt.To = ParseUsTime(strTo);

			var attendeesText = htmlText.TakeStringBetween("<table width=100%>", "</table>");
			var attendessDoc = new HtmlDocument();
			attendessDoc.LoadHtml(attendeesText);
			var attendeesTable = new HtmlTable(attendessDoc.DocumentNode);

			foreach (var row in attendeesTable)
			{
				var name = row[0]?.InnerText?.Trim();
				var nameHref = row[0]?.SelectSingleNode("a")?.GetAttributeValue("href", ""); // http://taw.net/event/66327.aspx last row, unit name has no link
				if (nameHref != null && nameHref.StartsWith("/member"))
				{
					var person = GetPersonFromName(data, name);

					var personToEvent = data.PersonEvents.FirstOrDefault(p => p.Event.EventId == evt.EventId && p.Person.PersonId == person.PersonId);
					if (personToEvent == null)
					{
						personToEvent = new PersonEvent();
						personToEvent.Event = evt;
						personToEvent.Person = person;
						personToEvent = data.PersonEvents.Add(personToEvent);
					}

					var attendanceStr = row[1]?.InnerText?.Trim();
					AttendanceType attendanceType = AttendanceType.Unknown;
					if (attendanceStr != null && Enum.TryParse(attendanceStr.ToLowerInvariant(), true, out attendanceType)) personToEvent.AttendanceType = attendanceType;

					var timestampStr = row[2]?.InnerText?.Trim();
					if (!timestampStr.Contains("--"))
					{
						try
						{
							personToEvent.TimeStamp = DateTime.ParseExact(timestampStr, "M-d-yyyy hh:mm", CultureInfo.InvariantCulture, DateTimeStyles.None);
						}
						catch (Exception e)
						{
							Log.Fatal($"failed to parse time:{timestampStr} exception:{e}");
						}
					}
				}
				else if (nameHref != null && nameHref.StartsWith("/unit"))
				{
					var unitTawIdStr = nameHref.Split('/', '\\').Last().RemoveFromEnd(".aspx".Length);
					var unitTawId = int.Parse(unitTawIdStr);
					var unit = GetUnit(data, unitTawId, name);
					(evt.Units ?? (evt.Units = new List<Unit>())).Add(unit);
				}
				else if (nameHref == null)
				{
					// event with no unit
				}
				else
				{
					throw new Exception("something is wrong, found unexpected data");
				}
			}
		}
	}
}