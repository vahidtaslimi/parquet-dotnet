﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using Parquet.Data;
using Parquet.Data.Rows;
using static System.Reflection.Emit.OpCodes;

namespace Parquet.Serialization.Values
{
   class MSILGenerator
   {
      public delegate object PopulateListDelegate(object instances,
         object resultItemsList,
         object resultRepetitionsList,
         int maxRepetitionLevel);

      public delegate void AssignArrayDelegate(
         DataColumn column,
         Array classInstances);

      public MSILGenerator()
      {
      }

      public PopulateListDelegate GenerateCollector(Type classType, DataField field)
      {
         Type[] methodArgs = { typeof(object), typeof(object), typeof(object), typeof(int) };

         TypeInfo ti = classType.GetTypeInfo();
         PropertyInfo pi = ti.GetDeclaredProperty(field.ClrPropName ?? field.Name);
         MethodInfo getValueMethod = pi.GetMethod;

         MethodInfo addToListMethod = typeof(List<>).MakeGenericType(field.ClrNullableIfHasNullsType).GetTypeInfo().GetDeclaredMethod("Add");
         MethodInfo addRepLevelMethod = typeof(List<int>).GetTypeInfo().GetDeclaredMethod("Add");

         var runMethod = new DynamicMethod(
            $"Get{classType.Name}{field.Path}",
            typeof(object),
            methodArgs,
            GetType().GetTypeInfo().Module);

         ILGenerator il = runMethod.GetILGenerator();

         GenerateCollector(il, classType,
            field,
            getValueMethod,
            addToListMethod,
            addRepLevelMethod);

         return (PopulateListDelegate)runMethod.CreateDelegate(typeof(PopulateListDelegate));
      }

      private void GenerateCollector(ILGenerator il, Type classType,
         DataField f,
         MethodInfo getValueMethod,
         MethodInfo addToListMethod,
         MethodInfo addRepLevelMethod)
      {
         //arg 0 - collection of classes, clr objects
         //arg 1 - data items (typed list)
         //arg 2 - repetitions (optional)
         //arg 3 - max repetition level

         //make collection a local variable
         LocalBuilder collection = il.DeclareLocal(classType);
         il.Emit(Ldarg_0);
         il.Emit(Stloc, collection.LocalIndex);

         //hold item
         LocalBuilder item = il.DeclareLocal(f.ClrNullableIfHasNullsType);

         //current repetition level
         LocalBuilder rl = null;
         if(f.IsArray)
         {
            rl = il.DeclareLocal(typeof(int));
         }

         using (il.ForEachLoop(classType, collection, out LocalBuilder currentElement))
         {
            if (f.IsArray)
            {
               //reset repetition level to 0
               il.Emit(Ldc_I4_0);
               il.StLoc(rl);

               //currentElement is a nested array in this case
               LocalBuilder array = il.DeclareLocal(typeof(object));

               //get array and put into arrayElement
               il.Emit(Ldloc, currentElement.LocalIndex);
               il.Emit(Callvirt, getValueMethod);
               il.Emit(Stloc, array.LocalIndex);

               //enumerate this array
               using (il.ForEachLoop(f.ClrNullableIfHasNullsType, array, out LocalBuilder arrayElement))
               {
                  //store in destination list
                  il.CallVirt(addToListMethod, Ldarg_1, arrayElement);

                  //add repetition level
                  il.CallVirt(addRepLevelMethod, Ldarg_2, rl);

                  //set repetition level to max
                  il.Emit(Ldarg_3);
                  il.StLoc(rl);
               }

            }
            else
            {
               //get current value
               il.Emit(Ldloc, currentElement.LocalIndex);
               il.Emit(Callvirt, getValueMethod);
               il.Emit(Stloc, item.LocalIndex);

               //store in destination list
               il.Emit(Ldarg_1);
               il.Emit(Ldloc, item.LocalIndex);
               il.Emit(Callvirt, addToListMethod);
            }
         }

         il.Emit(Ldc_I4_0);
         il.Emit(Ret);
      }

      public AssignArrayDelegate GenerateAssigner(DataColumn dataColumn, Type classType)
      {
         DataField field = dataColumn.Field;

         Type[] methodArgs = { typeof(DataColumn), typeof(Array) };
         var runMethod = new DynamicMethod(
            $"Set{classType.Name}{field.Name}",
            typeof(void),
            methodArgs,
            GetType().GetTypeInfo().Module);

         ILGenerator il = runMethod.GetILGenerator();

         //set class property method
         TypeInfo ti = classType.GetTypeInfo();
         PropertyInfo pi = ti.GetDeclaredProperty(field.ClrPropName ?? field.Name);
         MethodInfo setValueMethod = pi.SetMethod;

         TypeInfo dcti = dataColumn.GetType().GetTypeInfo();
         MethodInfo getDataMethod = dcti.GetDeclaredProperty(nameof(DataColumn.Data)).GetMethod;
         MethodInfo getRepsMethod = dcti.GetDeclaredProperty(nameof(DataColumn.RepetitionLevels)).GetMethod;

         GenerateAssigner(il, classType, field,
            setValueMethod,
            getDataMethod,
            getRepsMethod);

         return (AssignArrayDelegate)runMethod.CreateDelegate(typeof(AssignArrayDelegate));
      }

      private void GenerateAssigner(ILGenerator il, Type classType, DataField field,
         MethodInfo setValueMethod,
         MethodInfo getDataMethod,
         MethodInfo getRepsMethod)
      {
         //arg 0 - DataColumn
         //arg 1 - class intances array (Array)

         if (field.IsArray)
         {
            LocalBuilder repItem = il.DeclareLocal(typeof(int));
            LocalBuilder dce = il.DeclareLocal(typeof(DataColumnEnumerator));

            //we will use DataColumnEnumerator for complex types

            //create an instance of it
            il.Emit(Ldarg_0); //constructor argument
            il.Emit(Newobj, typeof(DataColumnEnumerator).GetTypeInfo().DeclaredConstructors.First());
            il.StLoc(dce);

            LocalBuilder ci = il.DeclareLocal(typeof(int)); //class index
            LocalBuilder classInstance = il.DeclareLocal(classType); //class instance

            using (il.ForEachLoopFromEnumerator(typeof(object), dce, out LocalBuilder element))
            {
               //element should be an array for simple repeatables

               //get class instance by index
               il.GetArrayElement(Ldarg_1, ci, true, typeof(Array), classInstance);

               //assign data item to class property
               il.CallVirt(setValueMethod, classInstance, element);

               il.Increment(ci);
            }

         }
         else
         {
            //get values
            LocalBuilder data = il.DeclareLocal(typeof(Array));
            il.CallVirt(getDataMethod, Ldarg_0);
            il.StLoc(data);

            //get length of values
            LocalBuilder dataLength = il.DeclareLocal(typeof(int));
            il.GetArrayLength(data, dataLength);

            LocalBuilder dataItem = il.DeclareLocal(field.ClrNullableIfHasNullsType);  //current value
            bool dataIsRef = field.HasNulls && !field.ClrNullableIfHasNullsType.IsSystemNullable();

            LocalBuilder classInstance = il.DeclareLocal(classType);

            using (il.ForLoop(dataLength, out LocalBuilder iData))
            {
               //get data value
               il.GetArrayElement(data, iData, dataIsRef, field.ClrNullableIfHasNullsType, dataItem);

               //get class instance
               il.GetArrayElement(Ldarg_1, iData, true, classType, classInstance);

               //assign data item to class property
               il.CallVirt(setValueMethod, classInstance, dataItem);
            }
         }

         il.Emit(Ret);
      }
   }
}
