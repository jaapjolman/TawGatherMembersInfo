delimiter //
drop procedure if exists AttendanceReport//
create procedure AttendanceReport(in rootUnitId bigint(20), in daysBackTo int(10)) -- , in daysBackFrom int(10)
begin
	   
		   
	declare selected_PersonId bigint(20); 
	declare cursor_end tinyint(1);
	
	declare totalMandatories bigint(20);
	declare totalAnyEvent bigint(20);
	
	declare startDate datetime;
	declare endDate datetime;
	
	declare selected_people cursor for
		select p.PersonId from People p
		join PersonUnits pu on pu.Person_PersonId = p.PersonId and pu.Unit_UnitId in
		(
			select * from
				(select battalion.UnitId from Units battalion where battalion.TawId = rootUnitId) a
			union all
				(select platoon.UnitId from Units battalion
				join Units platoon on battalion.UnitId = platoon.ParentUnit_UnitId and battalion.TawId = rootUnitId)
			union all
				(select squad.UnitId from Units battalion
				join Units platoon on battalion.UnitId = platoon.ParentUnit_UnitId and battalion.TawId = rootUnitId 
				join Units squad on platoon.UnitId = squad.ParentUnit_UnitId)
			union all
				(select fireteam.UnitId from Units battalion
				join Units platoon on battalion.UnitId = platoon.ParentUnit_UnitId and battalion.TawId = rootUnitId 
				join Units squad on platoon.UnitId = squad.ParentUnit_UnitId
				join Units fireteam on squad.UnitId = fireteam.ParentUnit_UnitId)
		)
		group by p.PersonId
		order by name;
		
	declare continue handler for not found set cursor_end = true;
	
	create temporary table if not exists attendanceReportResult (
		UnitName varchar(100),
		UserName varchar(500),
		RankNameShort varchar(10),
		Trainings bigint(20),
		Attended bigint(20),
		Excused bigint(20),
		AWOL bigint(20),
		MandatoryAVG float,
		TotalAVG float,
		DaysInRank bigint(20)
	);
	
	truncate table attendanceReportResult;
	select (date_sub(now(), interval daysBackTo day)) into startDate;
	
	open selected_people;
	read_loop: LOOP

		fetch selected_people into selected_PersonId;		
				
		if cursor_end then
			leave read_loop;
		end if;     
					   
		select count(*) from PersonEvents pe join People p on p.PersonId = selected_PersonId and p.PersonId = pe.PersonId join Events e on e.EventId = pe.EventId and e.From > startDate
		and e.Mandatory         
		into totalMandatories;
		
		select count(*) from PersonEvents pe join People p on p.PersonId = selected_PersonId and p.PersonId = pe.PersonId join Events e on e.EventId = pe.EventId and e.From > startDate
		into totalAnyEvent;
		
		insert into attendanceReportResult values (
		
			(select u.Name from Units u where u.TawId = rootUnitId),
			(select p.Name from People p where p.PersonId = selected_PersonId),
			(select pr.NameShort from People p join PersonRanks pr on p.PersonId = pr.Person_PersonId order by pr.ValidFrom limit 1),
			
			(select count(*) from PersonEvents pe join People p on p.PersonId = selected_PersonId and p.PersonId = pe.PersonId join Events e on e.EventId = pe.EventId and e.From > startDate
			),
			
			(select count(*) from PersonEvents pe join People p on p.PersonId = selected_PersonId and p.PersonId = pe.PersonId join Events e on e.EventId = pe.EventId and e.From > startDate
			and pe.AttendanceType = 1),
			
			(select count(*) from PersonEvents pe join People p on p.PersonId = selected_PersonId and p.PersonId = pe.PersonId join Events e on e.EventId = pe.EventId and e.From > startDate
			and pe.AttendanceType = 2),
			
			(select count(*) from PersonEvents pe join People p on p.PersonId = selected_PersonId and p.PersonId = pe.PersonId join Events e on e.EventId = pe.EventId and e.From > startDate
			and pe.AttendanceType = 3),
		
			IF(
				totalMandatories > 0,
				(select count(*) from PersonEvents pe join People p on p.PersonId = selected_PersonId and p.PersonId = pe.PersonId join Events e on e.EventId = pe.EventId and e.From > startDate
				and pe.AttendanceType = 1 and e.Mandatory) / totalMandatories,
				0
			),
			
			IF(
				totalAnyEvent > 0,
				(select count(*) from PersonEvents pe join People p on p.PersonId = selected_PersonId and p.PersonId = pe.PersonId join Events e on e.EventId = pe.EventId and e.From > startDate
				and pe.AttendanceType = 1) / totalAnyEvent,
				0
			),
			
			(select datediff(CURRENT_DATE, pr.ValidFrom) from People p join PersonRanks pr on p.PersonId = pr.Person_PersonId order by pr.ValidFrom limit 1)
		);

	end loop;
	
	close selected_people;
	
	select * from attendanceReportResult order by UserName;


end//
delimiter ;
