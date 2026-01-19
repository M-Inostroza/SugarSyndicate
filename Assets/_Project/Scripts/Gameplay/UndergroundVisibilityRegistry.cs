using System;
using System.Collections.Generic;
using UnityEngine;

public static class UndergroundVisibilityRegistry
{
    static readonly HashSet<MonoBehaviour> overlayTargets = new();
    static readonly HashSet<Conveyor> belts = new();
    static readonly HashSet<PowerCable> powerCables = new();
    static readonly HashSet<PowerPole> powerPoles = new();
    static readonly HashSet<DroneWorker> drones = new();
    static readonly HashSet<CrawlerWorker> crawlers = new();

    public static event Action<MonoBehaviour> OverlayRegistered;
    public static event Action<MonoBehaviour> OverlayUnregistered;
    public static event Action<Conveyor> BeltRegistered;
    public static event Action<Conveyor> BeltUnregistered;
    public static event Action<PowerCable> PowerCableRegistered;
    public static event Action<PowerCable> PowerCableUnregistered;
    public static event Action<PowerPole> PowerPoleRegistered;
    public static event Action<PowerPole> PowerPoleUnregistered;
    public static event Action<DroneWorker> DroneRegistered;
    public static event Action<DroneWorker> DroneUnregistered;
    public static event Action<CrawlerWorker> CrawlerRegistered;
    public static event Action<CrawlerWorker> CrawlerUnregistered;

    public static IReadOnlyCollection<MonoBehaviour> OverlayTargets => overlayTargets;
    public static IReadOnlyCollection<Conveyor> Belts => belts;
    public static IReadOnlyCollection<PowerCable> PowerCables => powerCables;
    public static IReadOnlyCollection<PowerPole> PowerPoles => powerPoles;
    public static IReadOnlyCollection<DroneWorker> Drones => drones;
    public static IReadOnlyCollection<CrawlerWorker> Crawlers => crawlers;

    public static void RegisterOverlay(MonoBehaviour target)
    {
        if (target == null) return;
        if (overlayTargets.Add(target))
            OverlayRegistered?.Invoke(target);
    }

    public static void UnregisterOverlay(MonoBehaviour target)
    {
        if (target == null) return;
        if (overlayTargets.Remove(target))
            OverlayUnregistered?.Invoke(target);
    }

    public static void RegisterBelt(Conveyor belt)
    {
        if (belt == null) return;
        if (belts.Add(belt))
            BeltRegistered?.Invoke(belt);
    }

    public static void UnregisterBelt(Conveyor belt)
    {
        if (belt == null) return;
        if (belts.Remove(belt))
            BeltUnregistered?.Invoke(belt);
    }

    public static void RegisterPowerCable(PowerCable cable)
    {
        if (cable == null) return;
        if (powerCables.Add(cable))
            PowerCableRegistered?.Invoke(cable);
    }

    public static void UnregisterPowerCable(PowerCable cable)
    {
        if (cable == null) return;
        if (powerCables.Remove(cable))
            PowerCableUnregistered?.Invoke(cable);
    }

    public static void RegisterPowerPole(PowerPole pole)
    {
        if (pole == null) return;
        if (powerPoles.Add(pole))
            PowerPoleRegistered?.Invoke(pole);
    }

    public static void UnregisterPowerPole(PowerPole pole)
    {
        if (pole == null) return;
        if (powerPoles.Remove(pole))
            PowerPoleUnregistered?.Invoke(pole);
    }

    public static void RegisterDrone(DroneWorker drone)
    {
        if (drone == null) return;
        if (drones.Add(drone))
            DroneRegistered?.Invoke(drone);
    }

    public static void UnregisterDrone(DroneWorker drone)
    {
        if (drone == null) return;
        if (drones.Remove(drone))
            DroneUnregistered?.Invoke(drone);
    }

    public static void RegisterCrawler(CrawlerWorker crawler)
    {
        if (crawler == null) return;
        if (crawlers.Add(crawler))
            CrawlerRegistered?.Invoke(crawler);
    }

    public static void UnregisterCrawler(CrawlerWorker crawler)
    {
        if (crawler == null) return;
        if (crawlers.Remove(crawler))
            CrawlerUnregistered?.Invoke(crawler);
    }
}
