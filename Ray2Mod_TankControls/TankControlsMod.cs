﻿using Ray2Mod;
using Ray2Mod.Components;
using Ray2Mod.Game;
using Ray2Mod.Game.Functions;
using Ray2Mod.Game.Structs.Input;
using Ray2Mod.Game.Structs.LinkedLists;
using Ray2Mod.Game.Structs.SPO;
using Ray2Mod.Game.Structs.States;
using Ray2Mod.Structs.Input;
using Ray2Mod.Utils;
using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;

/*
 * TODO: ledge pull up after climbing doesn't work
*/

namespace Ray2Mod_TankControls
{
   public unsafe class TankControlsMod : IMod
   {
      RemoteInterface ri;
      World w;
      HookManager hm;

      public const double quarterTau = (Math.PI / 2);
      public const float maxRotationSpeed = 0.075f;
      public const float rotationSpeedLerp = 0.5f;
      private float rotationSpeed = 0;
      private float currentSpeed = 0;
      private SuperObject* rayman;

      private bool playTurnAnimation = false;
      private bool gamePaused = false;

      private bool shiftToRun = false;

      private short walkBackFrame;

      private short ReadInputFunction(int a1)
      {
         var result = InputFunctions.VReadInput.Call(a1);

         //entryActions[(int)EntryActionNames.Action_Strafe]->validCount = 1;

         return result;
      }

      public int MarioFunction(SuperObject* spo, int* nodeInterp)
      {
         gamePaused = Marshal.ReadByte((IntPtr)Offsets.PauseScreen) > 0;

         RaymanState state = RaymanState.Inactive;
         byte dsgVar16_value = 0;
         int* idleTimer = null;
         EntryAction* shiftAction = *(EntryAction**)0x4B9B98;

         if (spo->PersoData->GetModelName(w) == "YLT_RaymanModel") {
            var dsgVars = spo->PersoData->GetDsgVarList();
            dsgVar16_value = *(byte*)dsgVars[16].valuePtrCurrent.ToPointer();
            byte dsgVar9_value = *(byte*)dsgVars[9].valuePtrCurrent.ToPointer();
            state = (RaymanState)dsgVar9_value;
            idleTimer = (int*)dsgVars[24].valuePtrCurrent;
         }

         bool strafing =
            InputStructure.GetInputStructure()->GetEntryAction(EntryActionNames.Action_Strafe)->IsValidated();

         int result;

         if (gamePaused || spo->PersoData->GetInstanceName(w) != "Rayman" || dsgVar16_value != 1 ||
             !AllowTankControlState(state)) {
            currentSpeed = 0;
            result = EngineFunctions.fn_p_stReadAnalogJoystickMario.Call(spo, nodeInterp);
         } else {
            InvertEntryAction(shiftAction);
            result = OriginalScript(spo, nodeInterp);
            DoTankControls(spo, state, strafing, idleTimer);
         }

         InvertEntryAction(shiftAction);

         return result;
      }

      private bool AllowTankControlState(RaymanState state)
      {
         return state == RaymanState.Idle ||
                state == RaymanState.Walking ||
                state == RaymanState.Running ||
                state == RaymanState.Strafing ||
                state == RaymanState.StrafingAndTargeting ||
                state == RaymanState.Helicoptering ||
                state == RaymanState.SuperHelicoptering ||
                state == RaymanState.JumpingUp ||
                state == RaymanState.FallingDown ||
                state == RaymanState.LedgeGrab ||
                state == RaymanState.CeilingClimbing ||
                state == RaymanState.CarryingObject ||
                state == RaymanState.ThrowingObject ||
                state == RaymanState.Swimming ||
                state == RaymanState.Sliding;
      }

      private void InvertEntryAction(EntryAction* action)
      {
         if (shiftToRun) {
            action->validCount = -action->validCount;
         }
      }

      private void DoTankControls(SuperObject* spo, RaymanState state, bool strafing, int* idleTimer)
      {
         EntryAction* leftAction = *(EntryAction**)0x4B9B90;
         EntryAction* rightAction = *(EntryAction**)0x4B9B94;
         EntryAction* forwardAction = *(EntryAction**)0x4B9B88;
         EntryAction* backAction = *(EntryAction**)0x4B9B8C;
         EntryAction* shiftAction = *(EntryAction**)0x4B9B98;

         playTurnAnimation = false;

         rayman = spo;

         ri.Log($"Rayman's state = " + rayman->PersoData->GetStateIndex());
         ri.Log($"Rayman's frame = " + rayman->PersoData->p3dData->currentFrame);

         var transformationMatrix = rayman->PersoData->dynam->DynamicsComplex->DynamicsBlockBaseBase.m_stCurrentMatrix
            .TransformationMatrix;
         var rotation = Quaternion.CreateFromRotationMatrix(transformationMatrix);

         float rotationSpeedTarget = ((leftAction->IsValidated() ? 1f : 0) + (rightAction->IsValidated() ? -1f : 0)) *
                                     maxRotationSpeed * (shiftAction->IsValidated() ? 0.75f : 1f);

         if (state == RaymanState.Swimming || state == RaymanState.Sliding) {
            rotationSpeedTarget *= 8.0f;
         }

         bool onPlumOrPlatform = false;
         if (rayman->parent != null && rayman->parent->type == SuperObjectType.Perso &&
             rayman->parent->PersoData != null) {
            string name = rayman->parent->PersoData->GetModelName(w).ToLower();
            onPlumOrPlatform = name == "IVP_plateforme".ToLower() || name == "BNT_ThePrune".ToLower();
         }

         if (onPlumOrPlatform) {
            rotationSpeedTarget *= 10.0f;
            forwardAction->validCount = 2;
         }

         // Interpolate to target speed
         rotationSpeed += (rotationSpeedTarget - rotationSpeed) * rotationSpeedLerp;

         if (state == RaymanState.LedgeGrab) {
            if (forwardAction->IsValidated()) {
               currentSpeed = 10;
            } else if (backAction->IsValidated()) {
               currentSpeed = -10;
            } else {
               currentSpeed = 0;
            }
         } else if (strafing && state != RaymanState.Swimming && state != RaymanState.Sliding) {
            float strafeX = ((leftAction->IsValidated() ? 1f : 0) + (rightAction->IsValidated() ? -1f : 0));
            float strafeY = ((forwardAction->IsValidated() ? 1f : 0) + (backAction->IsValidated() ? -1f : 0));

            float strafeMagnitude = (float)Math.Sqrt(strafeX * strafeX + strafeY * strafeY);

            if (strafeMagnitude == 0 || float.IsNaN(strafeMagnitude) || float.IsInfinity(strafeMagnitude)) {
               strafeMagnitude = 1.0f;
            }

            // Normalize and set length to 100
            strafeX *= 100.0f / strafeMagnitude;
            strafeY *= 100.0f / strafeMagnitude;

            HandleStrafing(rotation, rayman, strafeX, strafeY);

            return;
         } else {
            var entryActions = InputStructure.GetInputStructure()->EntryActions;

            bool walkingBack = false;

            // Can be turning?
            if (state == RaymanState.Idle) {
               int[] bannedActions = new int[]
                  { (int)EntryActionNames.Action_Tirer, (int)EntryActionNames.Action_Sauter };

               bool usingBannedAction = bannedActions.Where(a => entryActions[a]->IsValidated()).Count() > 0;

               int stateIndex = rayman->PersoData->GetStateIndex();
               // Shooting
               if (stateIndex == 190 || stateIndex == 193 || stateIndex == 194 || stateIndex == 257) {
                  usingBannedAction = true;
               }

               walkingBack = backAction->IsValidated() && state != RaymanState.CarryingObject && !usingBannedAction;

               if (!usingBannedAction) {
                  if (rightAction->IsValidated() && !leftAction->IsValidated()) {
                     if (rayman->PersoData->GetStateIndex() != 70) {
                        rayman->PersoData->SetState(70);
                     }

                     playTurnAnimation = true;
                  } else if (leftAction->IsValidated() && !rightAction->IsValidated()) {
                     if (rayman->PersoData->GetStateIndex() != 71) {
                        rayman->PersoData->SetState(71);
                     }

                     playTurnAnimation = true;
                  }

                  if (walkingBack) {
                     ri.Log($"walkBackFrame: " + walkBackFrame);

                     if (rayman->PersoData->GetStateIndex() != 62 || true) {
                        ri.Log($"Change state to 62");
                        rayman->PersoData->SetState(62);
                        //rayman->PersoData->NormalBehaviourIndex = 8;

                        EngineFunctions.PLA_fn_vSetCurrFrame.Call(rayman, (short)(walkBackFrame / 2));

                        //rayman->PersoData->p3dData->currentFrame = walkBackFrame;
                        walkBackFrame++;

                        if (walkBackFrame > 22) {
                           walkBackFrame = 0;
                        }

                        playTurnAnimation = true;
                     }
                  } else {
                     walkBackFrame = 0;
                  }
               }
            } else {
               walkingBack = backAction->IsValidated() && state != RaymanState.CarryingObject && state != RaymanState.ThrowingObject;
            }

            rotation *= Quaternion.CreateFromYawPitchRoll(0, 0, rotationSpeed); // Add rotation

            float targetSpeed =
               (forwardAction->IsValidated() ? 100f : (walkingBack ? (playTurnAnimation ? -33f : -100f) : 0f)) *
               (shiftAction->IsValidated() ? 0.5f : 1f);

            currentSpeed += (targetSpeed - currentSpeed) * 0.1f;
         }

         if (currentSpeed > 0 && currentSpeed < 0.5f) {
            currentSpeed = 0;
         }

         if (currentSpeed < 70 && Math.Abs(rotationSpeed) > 0.01f && state == RaymanState.Sliding) {
            currentSpeed = 70;
         }

         WriteVariables(rotation, rotationSpeed, transformationMatrix, rayman);
      }

      private static int OriginalScript(SuperObject* spo, int* nodeInterp)
      {
         // This is necessary because the script calls this function as "StdCamer.PAD_ReadAnalogJoystickMarioMode",
         // and the scripting engine keeps track of which object is executing a function (two ultra operator functions)
         *(int*)(Offsets.Globals.g_hCurrentSuperObjPerso) = 0;

         int* param = (int*)Marshal.AllocHGlobal(512);
         int* v4 = (int*)EngineFunctions.fn_p_stEvalTree.Call(spo, nodeInterp, param);
         int* v5 = (int*)EngineFunctions.fn_p_stEvalTree.Call(spo, v4, param);
         int* v7 = (int*)EngineFunctions.fn_p_stEvalTree.Call(spo, v5, param);
         int* v8 = (int*)EngineFunctions.fn_p_stEvalTree.Call(spo, v7, param);
         int* v9 = (int*)EngineFunctions.fn_p_stEvalTree.Call(spo, v8, param); // dsgVar_16 ? 1f : 0f

         int* v10 = (int*)EngineFunctions.fn_p_stEvalTree.Call(spo, v9, param);
         return EngineFunctions.fn_p_stEvalTree.Call(spo, v10, param);
      }

      private void HandleStrafing(Quaternion rotation, SuperObject* rayman, float strafeX, float strafeY)
      {
         float strafeSpeed = (float)Math.Sqrt(strafeX * strafeX + strafeY * strafeY);

         float* padAnalogForce = ((float*)0x4B9B78);
         *padAnalogForce = strafeSpeed;

         float* padTrueAnalogForce = ((float*)0x4B9B7C);
         *padTrueAnalogForce = strafeSpeed;

         float* padGlobalX = ((float*)0x4B9B68);
         float* padGlobalY = ((float*)0x4B9B6C);

         Vector3 rotationEuler = rotation.ToEuler();

         double strafeAngle = Math.Atan2(strafeY, strafeX);
         if (double.IsNaN(strafeAngle) || double.IsInfinity(strafeAngle)) {
            strafeAngle = 0;
         }

         *padGlobalX = strafeSpeed * (float)Math.Sin(rotationEuler.Z - strafeAngle + quarterTau);
         *padGlobalY = strafeSpeed * -(float)Math.Cos(rotationEuler.Z - strafeAngle + quarterTau);

         float* padRotationAngle = ((float*)0x4B9B80);
         *padRotationAngle = (float)(strafeAngle * 180 / Math.PI);

         int* padSector = ((int*)0x4B9B84);
         *padSector = GetPadSector(strafeX, strafeY);
      }

      private int GetPadSector(float strafeX, float strafeY)
      {
         if (Math.Abs(strafeX) > Math.Abs(strafeY)) {
            if (strafeX > 0) {
               return 4;
            } else {
               return 2;
            }
         } else {
            if (strafeY > 0) {
               return 1;
            } else {
               return 3;
            }
         }
      }

      private void WriteVariables(Quaternion rotation, float rotationDelta, Matrix4x4 transformationMatrix,
         SuperObject* rayman)
      {
         float* padAnalogForce = ((float*)0x4B9B78);
         *padAnalogForce = currentSpeed;

         float* padTrueAnalogForce = ((float*)0x4B9B7C);
         *padTrueAnalogForce = currentSpeed;
         float* padGlobalX = ((float*)0x4B9B68);
         float* padGlobalY = ((float*)0x4B9B6C);

         Vector3 rotationEuler = rotation.ToEuler();
         *padGlobalX = currentSpeed * (float)Math.Sin(rotationEuler.Z);
         *padGlobalY = currentSpeed * -(float)Math.Cos(rotationEuler.Z);

         float* padRotationAngle = ((float*)0x4B9B80);
         *padRotationAngle = rotationDelta * 100.0f;

         var newMatrix = Matrix4x4.CreateFromQuaternion(rotation);
         newMatrix.Translation = transformationMatrix.Translation;

         if (currentSpeed < 0) {
            var translation = newMatrix.Translation;

            float reverseSpeed = 1.0f / 1000.0f;

            translation.X += currentSpeed * (float)Math.Sin(rotationEuler.Z) * reverseSpeed;
            translation.Y += currentSpeed * -(float)Math.Cos(rotationEuler.Z) * reverseSpeed;
            newMatrix.Translation = translation;
         }

         rayman->PersoData->dynam->DynamicsComplex->DynamicsBlockBaseBase.m_stCurrentMatrix.TransformationMatrix =
            newMatrix;
         //rayman->PersoData->dynam->DynamicsComplex->DynamicsBlockBaseBase.m_stPreviousMatrix.TransformationMatrix = newMatrix;
      }

      private char PLA_fn_bSetNewState(SuperObject* persoSpo, LinkedList.ListElement_HHP<State>* state, char force,
         char withEvents, char setAnim)
      {
         if (persoSpo == rayman && playTurnAnimation) {
            int stateIndex = persoSpo->PersoData->GetStateIndex(state);
            if (stateIndex == 0) {
               ri.Log("Ignore state transition");
               return (char)0;
            }
         }

         return EngineFunctions.PLA_fn_bSetNewState.Call(persoSpo, state, force, withEvents, setAnim);
      }

      unsafe void IMod.Run(RemoteInterface remoteInterface)
      {
         ri = remoteInterface;

         ri.Log("Rayman 2 Tank Controls");

         w = new World();

         GlobalInput.Actions['p'] = () => { shiftToRun = !shiftToRun; };

         hm = new HookManager();
         hm.CreateHook(EngineFunctions.fn_p_stReadAnalogJoystickMario, MarioFunction);
         hm.CreateHook(InputFunctions.VReadInput, ReadInputFunction);

         object p = hm.CreateHook(EngineFunctions.PLA_fn_bSetNewState, PLA_fn_bSetNewState);
      }
   }
}